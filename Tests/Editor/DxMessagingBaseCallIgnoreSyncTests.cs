#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Settings;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    [TestFixture]
    public sealed class DxMessagingSettingsLegacyDiagnosticsTests
    {
        private readonly List<DxMessagingSettings> _createdSettings = new();

        [TearDown]
        public void TearDown()
        {
            foreach (DxMessagingSettings settings in _createdSettings)
            {
                if (settings != null)
                {
                    Object.DestroyImmediate(settings);
                }
            }
            _createdSettings.Clear();
        }

        [Test]
        public void DiagnosticsTargetsReflectsLegacyEditorDiagnosticsBeforeDurableMigration()
        {
            DxMessagingSettings settings = NewSettings();
            settings._diagnosticsTargets = DiagnosticsTarget.Off;
            settings._legacyEnableDiagnosticsInEditor = true;

            Assert.That(
                settings.DiagnosticsTargets,
                Is.EqualTo(DiagnosticsTarget.Editor),
                "Passive settings loads must expose the legacy enable-diagnostics intent "
                    + "immediately so globals never briefly fall back to Off before the deferred "
                    + "asset migration runs."
            );
            Assert.That(
                settings._legacyEnableDiagnosticsInEditor,
                Is.True,
                "Reading the effective diagnostics target must remain mutation-free; durable "
                    + "migration is performed only by GetOrCreateSettings on an idle editor tick."
            );
        }

        [Test]
        public void ApplyLegacyDiagnosticsMigrationClearsLegacyFlagAndPromotesOffToEditor()
        {
            DxMessagingSettings settings = NewSettings();
            settings._diagnosticsTargets = DiagnosticsTarget.Off;
            settings._legacyEnableDiagnosticsInEditor = true;

            bool migrated = settings.ApplyLegacyDiagnosticsMigration();

            Assert.That(migrated, Is.True);
            Assert.That(settings._diagnosticsTargets, Is.EqualTo(DiagnosticsTarget.Editor));
            Assert.That(settings.DiagnosticsTargets, Is.EqualTo(DiagnosticsTarget.Editor));
            Assert.That(settings._legacyEnableDiagnosticsInEditor, Is.False);
        }

        [Test]
        public void ApplyLegacyDiagnosticsMigrationPreservesNonOffDiagnosticsTargets()
        {
            DxMessagingSettings settings = NewSettings();
            settings._diagnosticsTargets = DiagnosticsTarget.All;
            settings._legacyEnableDiagnosticsInEditor = true;

            bool migrated = settings.ApplyLegacyDiagnosticsMigration();

            Assert.That(migrated, Is.True);
            Assert.That(settings._diagnosticsTargets, Is.EqualTo(DiagnosticsTarget.All));
            Assert.That(settings.DiagnosticsTargets, Is.EqualTo(DiagnosticsTarget.All));
            Assert.That(settings._legacyEnableDiagnosticsInEditor, Is.False);
        }

        [Test]
        public void DiagnosticsTargetsSetterClearsPendingLegacyDiagnosticsFlag()
        {
            DxMessagingSettings settings = NewSettings();
            settings._diagnosticsTargets = DiagnosticsTarget.Off;
            settings._legacyEnableDiagnosticsInEditor = true;

            settings.DiagnosticsTargets = DiagnosticsTarget.Off;

            Assert.That(settings.DiagnosticsTargets, Is.EqualTo(DiagnosticsTarget.Off));
            Assert.That(settings._legacyEnableDiagnosticsInEditor, Is.False);
        }

        private DxMessagingSettings NewSettings()
        {
            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            _createdSettings.Add(settings);
            return settings;
        }
    }

    [TestFixture]
    public sealed class DxMessagingEditorInitializerTests
    {
        private readonly List<Action> _scheduled = new();
        private readonly List<DxMessagingSettings> _createdSettings = new();
        private readonly List<string> _warnings = new();
        private DiagnosticsTarget _previousDiagnosticsTargets;
        private int _previousMessageBufferSize;

        [SetUp]
        public void SetUp()
        {
            _previousDiagnosticsTargets = IMessageBus.GlobalDiagnosticsTargets;
            _previousMessageBufferSize = IMessageBus.GlobalMessageBufferSize;
            _scheduled.Clear();
            _warnings.Clear();

            DxMessagingEditorInitializer.ResetTestSeams();
            DxMessagingEditorInitializer.StaticStateResetter = () =>
            {
                IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
                IMessageBus.GlobalMessageBufferSize = IMessageBus.DefaultMessageBufferSize;
            };
            DxMessagingEditorInitializer.AssetDatabaseMutationScheduler = work =>
                _scheduled.Add(work);
            DxMessagingEditorInitializer.DomainReloadDisabledDetector = () => false;
            DxMessagingEditorInitializer.DomainReloadWarningLogger = message =>
                _warnings.Add(message);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                foreach (DxMessagingSettings settings in _createdSettings)
                {
                    if (settings != null)
                    {
                        Object.DestroyImmediate(settings);
                    }
                }
                _createdSettings.Clear();
            }
            finally
            {
                DxMessagingEditorInitializer.ResetTestSeams();
                IMessageBus.GlobalDiagnosticsTargets = _previousDiagnosticsTargets;
                IMessageBus.GlobalMessageBufferSize = _previousMessageBufferSize;
            }
        }

        [Test]
        public void ApplyEditorSettingsUsesPassiveLegacyDiagnosticsValueImmediately()
        {
            DxMessagingSettings settings = NewSettings();
            settings._diagnosticsTargets = DiagnosticsTarget.Off;
            settings._legacyEnableDiagnosticsInEditor = true;
            settings._messageBufferSize = 37;
            DxMessagingEditorInitializer.PassiveSettingsLoader = () => settings;

            DxMessagingEditorInitializer.ApplyEditorSettings();

            Assert.That(IMessageBus.GlobalDiagnosticsTargets, Is.EqualTo(DiagnosticsTarget.Editor));
            Assert.That(IMessageBus.GlobalMessageBufferSize, Is.EqualTo(37));
            Assert.That(
                settings._legacyEnableDiagnosticsInEditor,
                Is.True,
                "The passive initializer path must not perform durable asset migration; it only "
                    + "applies effective values and schedules the idle mutation."
            );
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(1),
                "ApplyEditorSettings must schedule exactly one deferred settings ensure."
            );
        }

        [Test]
        public void ApplyEditorSettingsWhenPassiveSettingsMissingDefersEnsureWithoutCreatingSynchronously()
        {
            int ensureCount = 0;
            DxMessagingEditorInitializer.PassiveSettingsLoader = () => null;
            DxMessagingEditorInitializer.SettingsAssetEnsurer = () =>
            {
                ensureCount++;
                return NewSettings();
            };
            IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.All;
            IMessageBus.GlobalMessageBufferSize = 3;

            DxMessagingEditorInitializer.ApplyEditorSettings();

            Assert.That(
                ensureCount,
                Is.EqualTo(0),
                "The domain-load path must stay passive and defer settings asset creation."
            );
            Assert.That(_scheduled.Count, Is.EqualTo(1));
            Assert.That(IMessageBus.GlobalDiagnosticsTargets, Is.EqualTo(DiagnosticsTarget.Off));
            Assert.That(
                IMessageBus.GlobalMessageBufferSize,
                Is.EqualTo(IMessageBus.DefaultMessageBufferSize)
            );
            Assert.That(
                _warnings,
                Is.Empty,
                "A missing passive settings asset cannot decide the warning preference; the "
                    + "deferred ensure callback re-checks with the realized settings."
            );
        }

        [Test]
        public void ApplyEditorSettingsWarnsImmediatelyWhenPassiveSettingsUnsuppressedAndDomainReloadDisabled()
        {
            int ensureCount = 0;
            DxMessagingSettings settings = NewSettings();
            settings._suppressDomainReloadWarning = false;
            DxMessagingEditorInitializer.PassiveSettingsLoader = () => settings;
            DxMessagingEditorInitializer.SettingsAssetEnsurer = () =>
            {
                ensureCount++;
                return settings;
            };
            DxMessagingEditorInitializer.DomainReloadDisabledDetector = () => true;

            DxMessagingEditorInitializer.ApplyEditorSettings();

            Assert.That(
                _warnings,
                Is.EqualTo(new[] { DxMessagingEditorInitializer.DomainReloadWarningMessage })
            );
            Assert.That(_scheduled.Count, Is.EqualTo(1));

            _scheduled[0].Invoke();

            Assert.That(ensureCount, Is.EqualTo(1));
            Assert.That(
                _warnings,
                Is.EqualTo(new[] { DxMessagingEditorInitializer.DomainReloadWarningMessage }),
                "The deferred ensure must not duplicate a warning already emitted from the "
                    + "domain-load passive settings read."
            );
        }

        [Test]
        public void DeferredSettingsEnsureUsesSharedIdleSchedulerAndRequeuesUntilIdle()
        {
            bool editorIdle = false;
            int ensureCount = 0;
            DxMessagingSettings ensuredSettings = NewSettings();
            ensuredSettings._diagnosticsTargets = DiagnosticsTarget.Runtime;
            ensuredSettings._messageBufferSize = 19;

            DxMessagingEditorInitializer.PassiveSettingsLoader = () => null;
            DxMessagingEditorInitializer.SettingsAssetEnsurer = () =>
            {
                ensureCount++;
                return ensuredSettings;
            };
            DxMessagingEditorInitializer.AssetDatabaseMutationScheduler = work =>
                DxMessagingEditorIdle.ScheduleAssetDatabaseMutation(
                    work,
                    scheduledWork => _scheduled.Add(scheduledWork),
                    () => editorIdle
                );

            DxMessagingEditorInitializer.ApplyEditorSettings();

            Assert.That(_scheduled.Count, Is.EqualTo(1));
            _scheduled[0].Invoke();

            Assert.That(
                ensureCount,
                Is.EqualTo(0),
                "The deferred settings ensure must not run while the editor is compiling/updating."
            );
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(2),
                "A busy deferred tick must requeue through DxMessagingEditorIdle."
            );

            editorIdle = true;
            _scheduled[1].Invoke();

            Assert.That(ensureCount, Is.EqualTo(1));
            Assert.That(
                IMessageBus.GlobalDiagnosticsTargets,
                Is.EqualTo(DiagnosticsTarget.Runtime)
            );
            Assert.That(IMessageBus.GlobalMessageBufferSize, Is.EqualTo(19));
            Assert.That(_scheduled.Count, Is.EqualTo(2));

            DxMessagingEditorInitializer.ApplyEditorSettings();
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(3),
                "A completed deferred ensure must clear its debounce latch so a later settings "
                    + "refresh can schedule another migration/create pass."
            );
        }

        [Test]
        public void DeferredSettingsEnsureWarnsOnceWhenDomainReloadIsDisabledAndWarningUnsuppressed()
        {
            int ensureCount = 0;
            DxMessagingSettings ensuredSettings = NewSettings();
            ensuredSettings._diagnosticsTargets = DiagnosticsTarget.Runtime;
            ensuredSettings._messageBufferSize = 23;
            ensuredSettings._suppressDomainReloadWarning = false;

            DxMessagingEditorInitializer.PassiveSettingsLoader = () => null;
            DxMessagingEditorInitializer.SettingsAssetEnsurer = () =>
            {
                ensureCount++;
                return ensuredSettings;
            };
            DxMessagingEditorInitializer.DomainReloadDisabledDetector = () => true;

            DxMessagingEditorInitializer.ApplyEditorSettings();

            Assert.That(_warnings, Is.Empty);
            Assert.That(_scheduled.Count, Is.EqualTo(1));

            _scheduled[0].Invoke();

            Assert.That(ensureCount, Is.EqualTo(1));
            Assert.That(
                IMessageBus.GlobalDiagnosticsTargets,
                Is.EqualTo(DiagnosticsTarget.Runtime)
            );
            Assert.That(IMessageBus.GlobalMessageBufferSize, Is.EqualTo(23));
            Assert.That(
                _warnings,
                Is.EqualTo(new[] { DxMessagingEditorInitializer.DomainReloadWarningMessage })
            );

            DxMessagingEditorInitializer.ApplyEditorSettings();
            Assert.That(_scheduled.Count, Is.EqualTo(2));
            _scheduled[1].Invoke();

            Assert.That(ensureCount, Is.EqualTo(2));
            Assert.That(
                _warnings,
                Is.EqualTo(new[] { DxMessagingEditorInitializer.DomainReloadWarningMessage }),
                "The warning must be re-evaluated after deferred settings ensure, but only emitted "
                    + "once per editor domain."
            );
        }

        [Test]
        public void DeferredSettingsEnsureKeepsDefaultSuppressedDomainReloadWarningQuiet()
        {
            DxMessagingEditorInitializer.PassiveSettingsLoader = () => null;
            DxMessagingEditorInitializer.SettingsAssetEnsurer = NewSettings;
            DxMessagingEditorInitializer.DomainReloadDisabledDetector = () => true;

            DxMessagingEditorInitializer.ApplyEditorSettings();
            _scheduled[0].Invoke();

            Assert.That(
                _warnings,
                Is.Empty,
                "Fresh settings assets default to suppressing this advisory warning unless the "
                    + "user clears the suppression setting."
            );
        }

        private DxMessagingSettings NewSettings()
        {
            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            _createdSettings.Add(settings);
            return settings;
        }
    }

    /// <summary>
    /// Regression coverage for issue #210: a synchronous <c>AssetDatabase.ImportAsset</c> issued
    /// from <see cref="DxMessagingSettings"/>'s <c>OnValidate</c> during domain-load asset
    /// deserialization re-enters the asset importer and hard-crashes the native editor
    /// (<c>GuidReservations::Reserve</c> abort on Unity 6000.4+).
    /// </summary>
    /// <remarks>
    /// The native crash cannot be reproduced from managed test code, so instead of provoking it we
    /// pin the invariant that makes it impossible: the OnValidate / deserialization-context path
    /// must DEFER the sidecar write to the next editor tick and never apply it synchronously. The
    /// tests substitute the deferral + apply seams on <see cref="DxMessagingBaseCallIgnoreSync"/> so
    /// they observe call timing deterministically and never write the on-disk sidecar.
    /// </remarks>
    [TestFixture]
    public sealed class DxMessagingBaseCallIgnoreSyncTests
    {
        private Action<Action> _originalScheduler;
        private Func<bool> _originalCanMutateAssetDatabase;
        private Action<DxMessagingSettings> _originalApplier;
        private Action _originalCscRspAdditionalFileSyncScheduler;
        private readonly List<Action> _scheduled = new();
        private readonly List<DxMessagingSettings> _createdSettings = new();
        private int _applyCount;
        private int _cscRspSyncCount;

        [SetUp]
        public void SetUp()
        {
            _originalScheduler = DxMessagingBaseCallIgnoreSync.DeferralScheduler;
            _originalCanMutateAssetDatabase = DxMessagingBaseCallIgnoreSync.CanMutateAssetDatabase;
            _originalApplier = DxMessagingBaseCallIgnoreSync.SidecarApplier;
            _originalCscRspAdditionalFileSyncScheduler =
                DxMessagingBaseCallIgnoreSync.CscRspAdditionalFileSyncScheduler;
            _scheduled.Clear();
            _applyCount = 0;
            _cscRspSyncCount = 0;

            // Capture deferred work instead of routing it to EditorApplication.delayCall, and count
            // applies instead of touching the AssetDatabase. This makes "synchronous vs deferred"
            // observable and keeps the tests filesystem-free and deterministic. The csc.rsp follow-up
            // is captured for the same reason.
            DxMessagingBaseCallIgnoreSync.DeferralScheduler = work => _scheduled.Add(work);
            DxMessagingBaseCallIgnoreSync.CanMutateAssetDatabase = () => true;
            DxMessagingBaseCallIgnoreSync.SidecarApplier = _ => _applyCount++;
            DxMessagingBaseCallIgnoreSync.CscRspAdditionalFileSyncScheduler = () =>
                _cscRspSyncCount++;
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                foreach (DxMessagingSettings settings in _createdSettings)
                {
                    if (settings != null)
                    {
                        Object.DestroyImmediate(settings);
                    }
                }
                _createdSettings.Clear();
            }
            finally
            {
                // Restore in finally so a throw while destroying objects can never leak the
                // substituted seams into the rest of the editor session.
                DxMessagingBaseCallIgnoreSync.DeferralScheduler = _originalScheduler;
                DxMessagingBaseCallIgnoreSync.CanMutateAssetDatabase =
                    _originalCanMutateAssetDatabase;
                DxMessagingBaseCallIgnoreSync.SidecarApplier = _originalApplier;
                DxMessagingBaseCallIgnoreSync.CscRspAdditionalFileSyncScheduler =
                    _originalCscRspAdditionalFileSyncScheduler;
            }
        }

        [Test]
        public void OnValidateDefersSidecarRegenerationInsteadOfImportingSynchronously()
        {
            DxMessagingSettings settings = NewSettings();

            InvokePrivate(settings, "OnValidate");

            Assert.That(
                _applyCount,
                Is.EqualTo(0),
                "OnValidate must NOT write/import the sidecar synchronously: a synchronous "
                    + "AssetDatabase.ImportAsset from OnValidate during domain-load deserialization "
                    + "re-enters the asset importer and hard-crashes the native editor (#210)."
            );
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(1),
                "OnValidate must schedule exactly one deferred sidecar regeneration."
            );

            // The deferred work must be a REAL regeneration, not a dropped/no-op write: driving the
            // scheduled tick applies the sidecar exactly once.
            _scheduled[0].Invoke();
            Assert.That(
                _applyCount,
                Is.EqualTo(1),
                "OnValidate's deferred work must apply the sidecar exactly once when the editor "
                    + "tick runs (it must defer the real regeneration, not silently drop it)."
            );
            Assert.That(
                _cscRspSyncCount,
                Is.EqualTo(1),
                "Once OnValidate's deferred sidecar write runs, csc.rsp must be resynchronized so "
                    + "the analyzer receives the generated sidecar during the same editor session."
            );
        }

        [Test]
        public void OnEnableDoesNotRegenerateSidecar()
        {
            DxMessagingSettings settings = NewSettings();

            InvokePrivate(settings, "OnEnable");

            // OnEnable fires on EVERY domain reload and play-mode entry. It must neither apply nor
            // schedule a regeneration: the on-disk sidecar is already consistent, and regenerating
            // here would churn the AssetDatabase on every reload (and reintroduce #210 risk).
            Assert.That(_applyCount, Is.EqualTo(0), "OnEnable must not apply the sidecar.");
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(0),
                "OnEnable must not schedule a deferred sidecar regeneration."
            );
        }

        [Test]
        public void RegenerateSidecarDeferredNeverAppliesSynchronously()
        {
            DxMessagingSettings settings = NewSettings();

            DxMessagingBaseCallIgnoreSync.RegenerateSidecarDeferred(settings);

            Assert.That(
                _applyCount,
                Is.EqualTo(0),
                "RegenerateSidecarDeferred must defer; it must never apply the sidecar synchronously."
            );
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(1),
                "RegenerateSidecarDeferred must schedule exactly one deferred regeneration."
            );

            // Driving the deferred work (the next editor tick) performs the apply exactly once.
            _scheduled[0].Invoke();
            Assert.That(
                _applyCount,
                Is.EqualTo(1),
                "The deferred work must apply the sidecar exactly once when the editor tick runs."
            );
            Assert.That(
                _cscRspSyncCount,
                Is.EqualTo(1),
                "Deferred sidecar regeneration must schedule a csc.rsp sync after the sidecar "
                    + "apply runs."
            );
        }

        [Test]
        public void RegenerateSidecarDeferredRequeuesUntilEditorCanMutateAssets()
        {
            bool editorIdle = false;
            DxMessagingBaseCallIgnoreSync.CanMutateAssetDatabase = () => editorIdle;
            DxMessagingSettings settings = NewSettings();

            DxMessagingBaseCallIgnoreSync.RegenerateSidecarDeferred(settings);

            Assert.That(
                _applyCount,
                Is.EqualTo(0),
                "Deferred regeneration must not apply synchronously."
            );
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(1),
                "Pre-condition: the first deferred tick must be scheduled."
            );

            _scheduled[0].Invoke();

            Assert.That(
                _applyCount,
                Is.EqualTo(0),
                "A deferred tick that fires while Unity is compiling/updating must not import the sidecar."
            );
            Assert.That(
                _cscRspSyncCount,
                Is.EqualTo(0),
                "A busy deferred tick must not resync csc.rsp before the sidecar apply runs."
            );
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(2),
                "A deferred tick that fires while Unity is compiling/updating must requeue itself."
            );

            editorIdle = true;
            _scheduled[1].Invoke();

            Assert.That(
                _applyCount,
                Is.EqualTo(1),
                "Once Unity is idle, the requeued callback must apply exactly once."
            );
            Assert.That(
                _cscRspSyncCount,
                Is.EqualTo(1),
                "Once the requeued sidecar apply succeeds, csc.rsp must be resynchronized exactly once."
            );
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(2),
                "The idle apply must not keep rescheduling itself."
            );
        }

        [Test]
        public void RegenerateSidecarAppliesSynchronouslyOutsideUpdateAndCompile()
        {
            // Add/Remove ignored-type actions are explicit user edits (button clicks, Project
            // Settings UI), not deserialization callbacks, so the synchronous path is correct there
            // and must be preserved. EditMode tests run outside update/compile, exercising it.
            Assume.That(
                !EditorApplication.isUpdating && !EditorApplication.isCompiling,
                "Test must run outside an editor update/compile window to exercise the synchronous path."
            );
            DxMessagingSettings settings = NewSettings();

            DxMessagingBaseCallIgnoreSync.RegenerateSidecar(settings);

            Assert.That(
                _applyCount,
                Is.EqualTo(1),
                "RegenerateSidecar must apply synchronously when Unity is idle (immediate feedback "
                    + "for explicit Add/Remove-ignored-type actions)."
            );
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(0),
                "RegenerateSidecar must not defer when Unity is idle."
            );
            Assert.That(
                _cscRspSyncCount,
                Is.EqualTo(1),
                "Synchronous sidecar regeneration must also schedule csc.rsp resynchronization."
            );
        }

        [Test]
        public void RegenerateSidecarDefersWhenEditorCannotMutateAssets()
        {
            bool editorIdle = false;
            DxMessagingBaseCallIgnoreSync.CanMutateAssetDatabase = () => editorIdle;
            DxMessagingSettings settings = NewSettings();

            DxMessagingBaseCallIgnoreSync.RegenerateSidecar(settings);

            Assert.That(
                _applyCount,
                Is.EqualTo(0),
                "RegenerateSidecar must not apply synchronously while Unity is compiling/updating."
            );
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(1),
                "RegenerateSidecar must defer while Unity is compiling/updating."
            );

            editorIdle = true;
            _scheduled[0].Invoke();

            Assert.That(
                _applyCount,
                Is.EqualTo(1),
                "The deferred regeneration must apply once Unity becomes idle."
            );
            Assert.That(
                _cscRspSyncCount,
                Is.EqualTo(1),
                "The deferred regeneration must schedule csc.rsp resynchronization after applying."
            );
        }

        [Test]
        public void RegenerateSidecarDeferredSkipsDestroyedSettings()
        {
            DxMessagingSettings settings = NewSettings();
            DxMessagingBaseCallIgnoreSync.RegenerateSidecarDeferred(settings);
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(1),
                "Pre-condition: a deferred regeneration must have been scheduled."
            );

            // Simulate the asset being destroyed (deleted, or torn down) before the deferred tick.
            Object.DestroyImmediate(settings);
            _createdSettings.Remove(settings);

            Assert.DoesNotThrow(
                () => _scheduled[0].Invoke(),
                "Deferred regeneration must tolerate the captured settings asset being destroyed "
                    + "before the editor tick fires."
            );
            Assert.That(
                _applyCount,
                Is.EqualTo(0),
                "Deferred regeneration must skip the apply when the captured settings asset has "
                    + "been destroyed (Unity's lifetime-aware null check)."
            );
            Assert.That(
                _cscRspSyncCount,
                Is.EqualTo(0),
                "Destroyed settings must not trigger a csc.rsp sync because no sidecar apply ran."
            );
        }

        private DxMessagingSettings NewSettings()
        {
            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            _createdSettings.Add(settings);
            // CreateInstance can fire OnEnable/OnValidate; drain anything captured so each test
            // starts from a clean slate.
            _scheduled.Clear();
            _applyCount = 0;
            _cscRspSyncCount = 0;
            return settings;
        }

        private static void InvokePrivate(DxMessagingSettings settings, string methodName)
        {
            MethodInfo method = typeof(DxMessagingSettings).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                method,
                Is.Not.Null,
                $"DxMessagingSettings.{methodName} was not found; if it was renamed, update this "
                    + "test (OnValidate/OnEnable are the #210-relevant deserialization callbacks)."
            );
            method.Invoke(settings, null);
        }
    }

    [TestFixture]
    public sealed class DxMessagingEditorIdleTests
    {
        [Test]
        public void ScheduleAssetDatabaseMutationRequeuesUntilIdle()
        {
            List<Action> scheduled = new();
            bool editorIdle = false;
            int runCount = 0;

            DxMessagingEditorIdle.ScheduleAssetDatabaseMutation(
                () => runCount++,
                work => scheduled.Add(work),
                () => editorIdle
            );

            Assert.That(scheduled.Count, Is.EqualTo(1));
            scheduled[0].Invoke();

            Assert.That(
                runCount,
                Is.EqualTo(0),
                "AssetDatabase work must not run while the editor reports a compile/update window."
            );
            Assert.That(
                scheduled.Count,
                Is.EqualTo(2),
                "Busy callbacks must requeue the asset mutation for a later editor tick."
            );

            editorIdle = true;
            scheduled[1].Invoke();

            Assert.That(runCount, Is.EqualTo(1));
            Assert.That(scheduled.Count, Is.EqualTo(2));
        }
    }

    /// <summary>
    /// Unit coverage for <see cref="DxMessagingBaseCallIgnoreSync.BuildContent"/>, the deterministic
    /// text payload the Roslyn analyzer consumes via <c>-additionalfile</c>. Pure and
    /// filesystem-free: it exercises ordering, dedupe, comment headers, and null/blank tolerance.
    /// </summary>
    [TestFixture]
    public sealed class DxMessagingBaseCallIgnoreSyncContentTests
    {
        private const string HeaderLine =
            "# Auto-generated from Assets/Editor/DxMessagingSettings.asset; edit there instead.";
        private const string FormatLine =
            "# One fully-qualified type name per line. Lines starting with # are comments.";

        [Test]
        public void BuildContentWritesBothCommentHeadersFirst()
        {
            string[] lines = SplitLines(
                DxMessagingBaseCallIgnoreSync.BuildContent(new List<string>())
            );

            Assert.That(lines.Length, Is.EqualTo(2), "Empty list must emit only the two headers.");
            Assert.That(lines[0], Is.EqualTo(HeaderLine));
            Assert.That(lines[1], Is.EqualTo(FormatLine));
        }

        [Test]
        public void BuildContentToleratesNullList()
        {
            Assert.DoesNotThrow(() => DxMessagingBaseCallIgnoreSync.BuildContent(null));
            string[] lines = SplitLines(DxMessagingBaseCallIgnoreSync.BuildContent(null));
            Assert.That(lines, Is.EqualTo(new[] { HeaderLine, FormatLine }));
        }

        [Test]
        public void BuildContentSortsOrdinalAndDeduplicatesPreservingHeaders()
        {
            string content = DxMessagingBaseCallIgnoreSync.BuildContent(
                new List<string> { "Zebra.Type", "Alpha.Type", "Zebra.Type", "Mid.Type" }
            );

            Assert.That(
                SplitLines(content),
                Is.EqualTo(
                    new[] { HeaderLine, FormatLine, "Alpha.Type", "Mid.Type", "Zebra.Type" }
                ),
                "Entries must be Ordinal-sorted and de-duplicated after the two headers."
            );

            // Pin the exact wire format the analyzer reads: LF-separated lines, one trailing LF, no
            // blank lines (the lenient SplitLines helper would otherwise mask a separator regression).
            Assert.That(
                content,
                Is.EqualTo(
                    HeaderLine
                        + "\n"
                        + FormatLine
                        + "\n"
                        + "Alpha.Type\n"
                        + "Mid.Type\n"
                        + "Zebra.Type\n"
                ),
                "BuildContent must emit LF-terminated lines with a single trailing LF and no blank lines."
            );
        }

        [Test]
        public void BuildContentSkipsNullAndWhitespaceEntriesAndTrimsRetained()
        {
            string[] lines = SplitLines(
                DxMessagingBaseCallIgnoreSync.BuildContent(
                    new List<string> { "  Keep.Type  ", "", "   ", null }
                )
            );

            Assert.That(
                lines,
                Is.EqualTo(new[] { HeaderLine, FormatLine, "Keep.Type" }),
                "Null/blank entries must be dropped and retained entries trimmed."
            );
        }

        private static string[] SplitLines(string content)
        {
            return content.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
#endif
