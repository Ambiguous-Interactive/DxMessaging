namespace DxMessaging.Editor
{
#if UNITY_EDITOR
    using System;
    using Core;
    using Core.MessageBus;
    using Settings;
    using UnityEditor;
    using UnityEngine;

    /// <summary>
    /// Applies DxMessaging Editor settings to global runtime defaults on domain load.
    /// </summary>
    [InitializeOnLoad]
    public static class DxMessagingEditorInitializer
    {
        private static bool s_playModeWarningIssued;
        private static bool s_ensureSettingsAssetScheduled;

        internal static Action StaticStateResetter { get; set; } = DxMessagingStaticState.Reset;
        internal static Func<DxMessagingSettings> PassiveSettingsLoader { get; set; } =
            DxMessagingSettings.LoadSettingsPassive;
        internal static Func<DxMessagingSettings> SettingsAssetEnsurer { get; set; } =
            DxMessagingSettings.GetOrCreateSettings;
        internal static Action<Action> AssetDatabaseMutationScheduler { get; set; } =
            DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;

        static DxMessagingEditorInitializer()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            ApplyEditorSettings();
            WarnIfDomainReloadDisabled();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange stateChange)
        {
            if (
                stateChange == PlayModeStateChange.EnteredEditMode
                || stateChange == PlayModeStateChange.ExitingEditMode
            )
            {
                ApplyEditorSettings();
                WarnIfDomainReloadDisabled();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplySettingsBeforeSceneLoad()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            ApplyEditorSettings();
        }

        internal static void ApplyEditorSettings()
        {
            (StaticStateResetter ?? DxMessagingStaticState.Reset)();

            // Issue #210: ApplyEditorSettings is reachable from the [InitializeOnLoad] static
            // constructor during the domain-load asset-import window, where a synchronous
            // AssetDatabase mutation -- the CreateAsset/SaveAssets/legacy-migration inside
            // GetOrCreateSettings -- can re-enter the importer and hard-crash the native editor
            // (GuidReservations::Reserve abort on Unity 6000.4+). Read current values via a
            // mutation-free passive load now; ensure the asset exists/migrates on the next editor
            // tick, off the import window.
            Func<DxMessagingSettings> passiveSettingsLoader =
                PassiveSettingsLoader ?? DxMessagingSettings.LoadSettingsPassive;
            ApplyGlobals(passiveSettingsLoader());
            ScheduleEnsureSettingsAsset();
        }

        private static void ScheduleEnsureSettingsAsset()
        {
            // Debounce: ApplyEditorSettings runs from the cctor and on every play-mode transition,
            // so coalesce to a single pending callback (mirrors the harvester's _rescanScheduled
            // latch) instead of stacking redundant delayCall registrations.
            if (s_ensureSettingsAssetScheduled)
            {
                return;
            }
            s_ensureSettingsAssetScheduled = true;
            Action<Action> scheduler =
                AssetDatabaseMutationScheduler
                ?? DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
            scheduler(EnsureSettingsAssetThenApplyGlobals);
        }

        private static void EnsureSettingsAssetThenApplyGlobals()
        {
            // Clear the latch BEFORE the work so a throw still leaves the next ApplyEditorSettings
            // free to reschedule (self-healing). GetOrCreateSettings touches the AssetDatabase, so
            // this callback must only be scheduled through DxMessagingEditorIdle's idle gate. Guard
            // defensively rather than letting an exception escape this editor callback.
            s_ensureSettingsAssetScheduled = false;
            try
            {
                Func<DxMessagingSettings> settingsAssetEnsurer =
                    SettingsAssetEnsurer ?? DxMessagingSettings.GetOrCreateSettings;
                ApplyGlobals(settingsAssetEnsurer());
            }
            catch (Exception ex)
            {
                DxMessagingEditorLog.LogWarning(
                    "Deferred settings initialization failed; will retry on the next editor settings refresh.",
                    ex
                );
            }
        }

        private static void ApplyGlobals(DxMessagingSettings settings)
        {
            if (settings == null)
            {
                return;
            }
            IMessageBus.GlobalDiagnosticsTargets = settings.DiagnosticsTargets;
            IMessageBus.GlobalMessageBufferSize = settings.MessageBufferSize;
        }

        internal static void ResetTestSeams()
        {
            s_ensureSettingsAssetScheduled = false;
            StaticStateResetter = DxMessagingStaticState.Reset;
            PassiveSettingsLoader = DxMessagingSettings.LoadSettingsPassive;
            SettingsAssetEnsurer = DxMessagingSettings.GetOrCreateSettings;
            AssetDatabaseMutationScheduler = DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
        }

        private static void WarnIfDomainReloadDisabled()
        {
            // Passive, mutation-free load: this is also reachable from the cctor (see #210 above).
            // A missing asset defaults to suppressing the warning, so treat null as "suppressed".
            DxMessagingSettings settings = DxMessagingSettings.LoadSettingsPassive();
            if (
                s_playModeWarningIssued
                || settings == null
                || settings.SuppressDomainReloadWarning
                || !EditorSettings.enterPlayModeOptionsEnabled
                || (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload)
                    == 0
            )
            {
                return;
            }

            s_playModeWarningIssued = true;
            Debug.LogWarning(
                "[DxMessaging] Enter Play Mode Options are disabling domain reload. "
                    + "DxMessaging resets its internal statics, but third-party static state will persist. "
                    + "Audit integration code or re-enable domain reload if inconsistent behaviour occurs."
            );
        }
    }
#endif
}
