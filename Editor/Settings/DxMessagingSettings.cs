namespace DxMessaging.Editor.Settings
{
#if UNITY_EDITOR
    using System.Collections.Generic;
    using System.Linq;
    using Core.MessageBus;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.Serialization;

    /// <summary>
    /// Project-wide DxMessaging settings asset (Editor-only).
    /// </summary>
    /// <remarks>
    /// Stored at <c>Assets/Editor/DxMessagingSettings.asset</c>. Controls global diagnostics defaults applied in the
    /// editor through <see cref="DxMessaging.Editor.DxMessagingEditorInitializer"/>.
    /// </remarks>
    public sealed class DxMessagingSettings : ScriptableObject
    {
        private const string SettingsPath = "Assets/Editor/DxMessagingSettings.asset";

        [SerializeField]
        internal DiagnosticsTarget _diagnosticsTargets = DiagnosticsTarget.Off;

        [SerializeField]
        [HideInInspector]
        [FormerlySerializedAs("_enableDiagnosticsInEditor")]
        internal bool _legacyEnableDiagnosticsInEditor;

        [SerializeField]
        internal int _messageBufferSize = IMessageBus.DefaultMessageBufferSize;

        [SerializeField]
        internal bool _suppressDomainReloadWarning = true;

        [SerializeField]
        internal List<string> _baseCallIgnoredTypes = new();

        [SerializeField]
        internal bool _baseCallCheckEnabled = true;

        [SerializeField]
        internal bool _useConsoleBridge;

        /// <summary>
        /// Controls <see cref="DiagnosticsTarget"/> values applied to <see cref="IMessageBus.GlobalDiagnosticsTargets"/>.
        /// </summary>
        public DiagnosticsTarget DiagnosticsTargets
        {
            get => EffectiveDiagnosticsTargets;
            set
            {
                _diagnosticsTargets = value;
                _legacyEnableDiagnosticsInEditor = false;
            }
        }

        /// <summary>
        /// Sets <see cref="Core.MessageBus.IMessageBus.GlobalMessageBufferSize"/> for Editor sessions.
        /// </summary>
        public int MessageBufferSize
        {
            get => _messageBufferSize;
            set => _messageBufferSize = value;
        }

        /// <summary>
        /// When true, suppresses the Enter Play Mode Options domain reload warning in the Editor.
        /// </summary>
        public bool SuppressDomainReloadWarning
        {
            get => _suppressDomainReloadWarning;
            set => _suppressDomainReloadWarning = value;
        }

        /// <summary>
        /// Master toggle for the <c>MessageAwareComponent</c> base-call check (DXMSG006/007/008).
        /// When <c>false</c>, the Inspector overlay and per-type warnings are silenced; the underlying
        /// compile-time analyzer warnings remain unless explicitly suppressed via <c>.editorconfig</c>.
        /// </summary>
        /// <remarks>
        /// S3: toggling from <c>false</c> back to <c>true</c> pokes
        /// <see cref="DxMessaging.Editor.Analyzers.DxMessagingConsoleHarvester"/> on the next editor
        /// tick so the snapshot repopulates without waiting for the user to clear/re-emit warnings
        /// or to manually invoke <c>Tools/Wallstop Studios/DxMessaging/Rescan Base-Call Warnings</c>. The round-trip
        /// is intentionally indirect (delayCall > RescanNow) to keep this property setter cheap and
        /// safe to invoke from any editor context -- including OnValidate, where AssetDatabase may
        /// be transitional.
        /// </remarks>
        public bool BaseCallCheckEnabled
        {
            get => _baseCallCheckEnabled;
            set
            {
                bool previous = _baseCallCheckEnabled;
                _baseCallCheckEnabled = value;
                if (!previous && value)
                {
                    // A master-toggle flip doesn't need a synchronous reflective harvest right now;
                    // the polled tick (~250ms) will pick up the sentinel cheaply on the editor's
                    // own update thread, avoiding a heavy reflection sweep on the main thread when
                    // the user has just clicked a checkbox. Indirected through delayCall so the
                    // setter is safe to invoke from any editor context (OnValidate, button click,
                    // etc.) without risking AssetDatabase reentrancy.
                    EditorApplication.delayCall += DxMessaging
                        .Editor
                        .Analyzers
                        .DxMessagingConsoleHarvester
                        .RequestRescan;
                }
            }
        }

        /// <summary>
        /// Opt-in toggle for the legacy console-scrape bridge that augments the IL-reflection
        /// scanner's snapshot with warnings harvested from <c>UnityEditor.LogEntries</c> and from
        /// <c>CompilationPipeline.assemblyCompilationFinished</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Default <c>false</c>. The IL-reflection scanner
        /// (<see cref="DxMessaging.Editor.Analyzers.BaseCallTypeScanner"/>) is the deterministic,
        /// always-on primary source -- it walks every loaded <c>MessageAwareComponent</c> subclass
        /// and inspects each override's IL body for the base-call shape, which is reliable across
        /// Unity 2021 cache hits, incremental compiles, and arbitrary domain-reload sequences.
        /// </para>
        /// <para>
        /// The legacy bridge predates the IL scanner and was the source of the intermittent
        /// "missing warnings" bug on Unity 2021. Enable it ONLY if you want the union of both
        /// data sources -- for example, to surface a regression in the IL byte-walker that is
        /// already correctly captured by the compile-time analyzer's console output.
        /// </para>
        /// <para>
        /// Toggling this property is observable via a deferred
        /// <see cref="DxMessaging.Editor.Analyzers.DxMessagingConsoleHarvester.ScheduleRescanWhenIdle"/>
        /// so the inspector overlay refreshes without waiting for the next compile.
        /// </para>
        /// </remarks>
        public bool UseConsoleBridge
        {
            get => _useConsoleBridge;
            set
            {
                if (_useConsoleBridge == value)
                {
                    return;
                }
                _useConsoleBridge = value;
                EditorUtility.SetDirty(this);
                DxMessaging.Editor.Analyzers.DxMessagingConsoleHarvester.ScheduleRescanWhenIdle();
            }
        }

        /// <summary>
        /// Fully-qualified type names excluded from the base-call check. Editable on the settings
        /// asset Inspector or via the Inspector overlay's "Ignore this type" button.
        /// </summary>
        public IReadOnlyList<string> BaseCallIgnoredTypes => _baseCallIgnoredTypes;

        /// <summary>
        /// Diagnostics target value after applying legacy serialized fields in memory. This does
        /// not mark the settings asset dirty or persist the migration.
        /// </summary>
        internal DiagnosticsTarget EffectiveDiagnosticsTargets
        {
            get
            {
                if (
                    _diagnosticsTargets == DiagnosticsTarget.Off
                    && _legacyEnableDiagnosticsInEditor
                )
                {
                    return DiagnosticsTarget.Editor;
                }
                return _diagnosticsTargets;
            }
        }

        /// <summary>
        /// Loads the settings asset without creating, saving, or migrating it. Returns
        /// <c>null</c> when no asset exists.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="GetOrCreateSettings"/>, this performs NO <c>AssetDatabase</c> mutation
        /// (<c>CreateAsset</c>/<c>SaveAssets</c>/durable legacy migration), so it is safe to call
        /// during the domain-load asset-import window -- e.g. from an <c>[InitializeOnLoad]</c>
        /// static constructor -- where a synchronous mutation re-enters the importer and crashes
        /// the native editor (issue #210). Legacy serialized fields are still reflected through
        /// effective property getters so passive callers see the intended value while the durable
        /// migration waits for a safe editor-idle tick. Callers that need the asset
        /// created/migrated must schedule <see cref="GetOrCreateSettings"/> off that window (e.g.
        /// via <see cref="DxMessaging.Editor.DxMessagingEditorIdle.ScheduleAssetDatabaseMutation"/>).
        /// </remarks>
        internal static DxMessagingSettings LoadSettingsPassive()
        {
            DxMessagingSettings settings = AssetDatabase.LoadAssetAtPath<DxMessagingSettings>(
                SettingsPath
            );

            if (settings == null)
            {
                settings = AssetDatabase
                    .FindAssets($"t:{nameof(DxMessagingSettings)}")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Select(AssetDatabase.LoadAssetAtPath<DxMessagingSettings>)
                    .FirstOrDefault(asset => asset != null);
            }

            return settings;
        }

        /// <summary>
        /// Loads the settings asset if present, otherwise creates it with sensible defaults.
        /// </summary>
        /// <remarks>
        /// Performs <c>AssetDatabase</c> mutations (create on first run, legacy-field migration), so
        /// it MUST NOT be called synchronously during the domain-load asset-import window (issue
        /// #210). Use <see cref="LoadSettingsPassive"/> for mutation-free reads in that context.
        /// </remarks>
        internal static DxMessagingSettings GetOrCreateSettings()
        {
            DxMessagingSettings settings = LoadSettingsPassive();

            if (settings == null)
            {
                settings = CreateInstance<DxMessagingSettings>();
                settings._diagnosticsTargets = DiagnosticsTarget.Off;
                settings._messageBufferSize = IMessageBus.DefaultMessageBufferSize;
                settings._suppressDomainReloadWarning = true;
                settings._baseCallCheckEnabled = true;
                settings._baseCallIgnoredTypes = new List<string>();
                if (!AssetDatabase.IsValidFolder("Assets/Editor"))
                {
                    AssetDatabase.CreateFolder("Assets", "Editor");
                }
                AssetDatabase.CreateAsset(settings, SettingsPath);
                AssetDatabase.SaveAssets();
            }

            if (settings.ApplyLegacyDiagnosticsMigration())
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            return settings;
        }

        internal bool ApplyLegacyDiagnosticsMigration()
        {
            if (!_legacyEnableDiagnosticsInEditor)
            {
                return false;
            }

            if (_diagnosticsTargets == DiagnosticsTarget.Off)
            {
                _diagnosticsTargets = DiagnosticsTarget.Editor;
            }
            _legacyEnableDiagnosticsInEditor = false;
            return true;
        }

        /// <summary>
        /// Returns a serialized wrapper for use in SettingsProvider inspectors.
        /// </summary>
        internal static SerializedObject GetSerializedSettings()
        {
            return new SerializedObject(GetOrCreateSettings());
        }

        private void OnEnable()
        {
            // Defensive: the field can be null if the asset was saved before this field existed.
            EnsureIgnoreListInitialized();
            // Intentionally NOT regenerating the sidecar here. OnEnable fires on every domain reload
            // and play-mode entry; the sidecar on disk is already consistent with what we'd write
            // (RegenerateSidecar is idempotent, but ImportAsset still produces churn). Regen runs
            // only from OnValidate (user-driven edits) and from explicit Add/RemoveIgnoredType calls.
        }

        private void OnValidate()
        {
            EnsureIgnoreListInitialized();
            // Issue #210: OnValidate fires during asset deserialization, including the domain-load
            // asset-import-worker window where EditorApplication.isUpdating/isCompiling are both
            // false. Writing + importing the sidecar synchronously there re-enters the asset
            // importer and hard-crashes the native editor (GuidReservations::Reserve abort on Unity
            // 6000.4+). Always defer to the next editor tick; never import synchronously from here.
            DxMessagingBaseCallIgnoreSync.RegenerateSidecarDeferred(this);
        }

        private void EnsureIgnoreListInitialized()
        {
            if (_baseCallIgnoredTypes == null)
            {
                _baseCallIgnoredTypes = new List<string>();
            }
        }

        /// <summary>
        /// Adds <paramref name="fullyQualifiedTypeName"/> to the ignore list, marks the asset
        /// dirty, saves, and regenerates the sidecar. No-op when the entry is already present.
        /// </summary>
        internal void AddIgnoredType(string fullyQualifiedTypeName)
        {
            if (string.IsNullOrWhiteSpace(fullyQualifiedTypeName))
            {
                return;
            }
            EnsureIgnoreListInitialized();
            if (
                _baseCallIgnoredTypes.Any(entry =>
                    string.Equals(entry, fullyQualifiedTypeName, System.StringComparison.Ordinal)
                )
            )
            {
                return;
            }
            _baseCallIgnoredTypes.Add(fullyQualifiedTypeName);
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
            TryRegenerateSidecar();
        }

        /// <summary>
        /// Removes <paramref name="fullyQualifiedTypeName"/> from the ignore list, marks the asset
        /// dirty, saves, and regenerates the sidecar. No-op when the entry is absent.
        /// </summary>
        internal void RemoveIgnoredType(string fullyQualifiedTypeName)
        {
            if (string.IsNullOrWhiteSpace(fullyQualifiedTypeName))
            {
                return;
            }
            if (_baseCallIgnoredTypes == null)
            {
                return;
            }
            int removed = _baseCallIgnoredTypes.RemoveAll(entry =>
                string.Equals(entry, fullyQualifiedTypeName, System.StringComparison.Ordinal)
            );
            if (removed > 0)
            {
                EditorUtility.SetDirty(this);
                AssetDatabase.SaveAssets();
                TryRegenerateSidecar();
            }
        }

        private void TryRegenerateSidecar()
        {
            try
            {
                DxMessagingBaseCallIgnoreSync.RegenerateSidecar(this);
            }
            catch (System.Exception ex)
            {
                DxMessaging.Editor.DxMessagingEditorLog.LogWarning(
                    "Failed to regenerate base-call ignore sidecar.",
                    ex
                );
            }
        }
    }
#endif
}
