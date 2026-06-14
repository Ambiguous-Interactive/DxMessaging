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
        internal const string DomainReloadWarningMessage =
            "[DxMessaging] Enter Play Mode Options are disabling domain reload. "
            + "DxMessaging resets its internal statics, but third-party static state will persist. "
            + "Audit integration code or re-enable domain reload if inconsistent behaviour occurs.";

        private static bool s_playModeWarningIssued;
        private static bool s_ensureSettingsAssetScheduled;

        internal static Action StaticStateResetter { get; set; } = DxMessagingStaticState.Reset;
        internal static Func<DxMessagingSettings> PassiveSettingsLoader { get; set; } =
            DxMessagingSettings.LoadSettingsPassive;
        internal static Func<DxMessagingSettings> SettingsAssetEnsurer { get; set; } =
            DxMessagingSettings.GetOrCreateSettings;
        internal static Action<Action> AssetDatabaseMutationScheduler { get; set; } =
            DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
        internal static Func<bool> DomainReloadDisabledDetector { get; set; } =
            IsDomainReloadDisabled;
        internal static Action<string> DomainReloadWarningLogger { get; set; } =
            message => Debug.LogWarning(message);

        static DxMessagingEditorInitializer()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            ApplyEditorSettings();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange stateChange)
        {
            if (
                stateChange == PlayModeStateChange.EnteredEditMode
                || stateChange == PlayModeStateChange.ExitingEditMode
            )
            {
                ApplyEditorSettings();
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
            ApplySettingsAndDiagnostics(passiveSettingsLoader());
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
                ApplySettingsAndDiagnostics(settingsAssetEnsurer());
            }
            catch (Exception ex)
            {
                DxMessagingEditorLog.LogWarning(
                    "Deferred settings initialization failed; will retry on the next editor settings refresh.",
                    ex
                );
            }
        }

        private static void ApplySettingsAndDiagnostics(DxMessagingSettings settings)
        {
            ApplyGlobals(settings);
            WarnIfDomainReloadDisabled(settings);
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
            s_playModeWarningIssued = false;
            s_ensureSettingsAssetScheduled = false;
            StaticStateResetter = DxMessagingStaticState.Reset;
            PassiveSettingsLoader = DxMessagingSettings.LoadSettingsPassive;
            SettingsAssetEnsurer = DxMessagingSettings.GetOrCreateSettings;
            AssetDatabaseMutationScheduler = DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
            DomainReloadDisabledDetector = IsDomainReloadDisabled;
            DomainReloadWarningLogger = message => Debug.LogWarning(message);
        }

        private static void WarnIfDomainReloadDisabled(DxMessagingSettings settings)
        {
            // Missing settings cannot be created from the domain-load passive path. The deferred
            // ensure callback re-enters this method with the realized asset, so an initial null
            // never permanently suppresses an explicitly unsuppressed settings asset.
            if (
                s_playModeWarningIssued
                || settings == null
                || settings.SuppressDomainReloadWarning
                || !(DomainReloadDisabledDetector ?? IsDomainReloadDisabled)()
            )
            {
                return;
            }

            s_playModeWarningIssued = true;
            Action<string> warningLogger = DomainReloadWarningLogger ?? Debug.LogWarning;
            warningLogger(DomainReloadWarningMessage);
        }

        private static bool IsDomainReloadDisabled()
        {
            return EditorSettings.enterPlayModeOptionsEnabled
                && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload)
                    != 0;
        }
    }
#endif
}
