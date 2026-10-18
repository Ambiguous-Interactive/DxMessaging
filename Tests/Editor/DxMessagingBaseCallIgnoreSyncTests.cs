#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Settings;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.TestTools;
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
        private readonly List<DxMessagingSettings> _createdSettings = new();
        private readonly List<string> _warnings = new();
        private EditorDelayCallScope _scheduled;
        private EditorStaticFieldScope _initializerState;
        private EditorSettingsCacheScope _settingsCache;
        private EditorStaticFieldScope _runtimeSettings;
        private UnityEngine.Object _previousRuntimeSettings;
        private EditorDelayCallScope _sidecarCallbacks;
        private IDisposable _registryScope;
        private IDisposable _poolScope;
        private DiagnosticsTarget _previousTargets;
        private bool _previousStacks;
        private int _previousBuffer;
        private int _previousBusIndex;
        private IMessageBus _previousBus;
        private bool _previousOptionsEnabled;
        private EnterPlayModeOptions _previousOptions;
        private bool _previousDebugEnabled;
        private Action<DxMessaging.Core.LogLevel, string> _previousDebugLog;
        private int _previousSyntheticOwner;

        [SetUp]
        public void SetUp()
        {
            _previousTargets = IMessageBus.GlobalDiagnosticsTargets;
            _previousStacks = IMessageBus.GlobalDiagnosticsStackTraces;
            _previousBuffer = IMessageBus.GlobalMessageBufferSize;
            _previousBusIndex = IMessageBus.GlobalSequentialIndex;
            _previousBus = DxMessaging.Core.MessageHandler.MessageBus;
            _previousDebugEnabled = DxMessaging.Core.MessagingDebug.enabled;
            _previousDebugLog = DxMessaging.Core.MessagingDebug.LogFunction;
            _previousSyntheticOwner =
                DxMessaging.Core.MessageBus.MessageRegistrationBuilder.GetSyntheticOwnerCounter();
            _previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _previousOptions = EditorSettings.enterPlayModeOptions;
            _initializerState = new(
                typeof(DxMessagingEditorInitializer),
                "s_playModeWarningIssued",
                "s_ensureSettingsAssetScheduled"
            );
            _initializerState.Set("s_playModeWarningIssued", false);
            _initializerState.Set("s_ensureSettingsAssetScheduled", false);
            _settingsCache = new();
            _runtimeSettings = new(
                typeof(DxMessaging.Core.Configuration.DxMessagingRuntimeSettingsProvider),
                "_cached"
            );
            _previousRuntimeSettings =
                MessageBusInspection.ReadField(
                    typeof(DxMessaging.Core.Configuration.DxMessagingRuntimeSettingsProvider),
                    "_cached"
                ) as UnityEngine.Object;
            _runtimeSettings.Set("_cached", null);
            _sidecarCallbacks = new(typeof(DxMessagingBaseCallIgnoreSync));
            _scheduled = new(typeof(DxMessagingEditorInitializer));
            _registryScope = MessageBusInspection.IsolateIdleSweepRegistryForBenchmark();
            _poolScope = MessageBusInspection.IsolateContextMapPoolForBenchmark();
            _warnings.Clear();
            Application.logMessageReceived += ObserveWarning;
            EditorSettings.enterPlayModeOptionsEnabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= ObserveWarning;
            try
            {
                _scheduled?.Dispose();
                _sidecarCallbacks?.Dispose();
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
                EditorTestCleanup.Run(
                    () =>
                    {
                        object currentRuntimeSettings = MessageBusInspection.ReadField(
                            typeof(DxMessaging.Core.Configuration.DxMessagingRuntimeSettingsProvider),
                            "_cached"
                        );
                        if (
                            currentRuntimeSettings
                                is DxMessaging.Core.Configuration.DxMessagingRuntimeSettings created
                            && created.IsFallbackInstance
                            && !ReferenceEquals(created, _previousRuntimeSettings)
                        )
                        {
                            Object.DestroyImmediate(created);
                        }
                    },
                    () => _runtimeSettings?.Dispose(),
                    () => _settingsCache?.Dispose(),
                    () => _initializerState?.Dispose(),
                    () => _poolScope?.Dispose(),
                    () => _registryScope?.Dispose(),
                    () => EditorSettings.enterPlayModeOptionsEnabled = _previousOptionsEnabled,
                    () => EditorSettings.enterPlayModeOptions = _previousOptions,
                    () => DxMessaging.Core.MessageHandler.SetGlobalMessageBus(_previousBus),
                    () => IMessageBus.GlobalSequentialIndex = _previousBusIndex,
                    () => IMessageBus.GlobalDiagnosticsTargets = _previousTargets,
                    () => IMessageBus.GlobalDiagnosticsStackTraces = _previousStacks,
                    () => IMessageBus.GlobalMessageBufferSize = _previousBuffer,
                    () => DxMessaging.Core.MessagingDebug.enabled = _previousDebugEnabled,
                    () => DxMessaging.Core.MessagingDebug.LogFunction = _previousDebugLog,
                    () =>
                        DxMessaging.Core.MessageBus.MessageRegistrationBuilder.SetSyntheticOwnerCounter(
                            _previousSyntheticOwner
                        )
                );
            }
        }

        private void ObserveWarning(string message, string stack, LogType type)
        {
            if (
                type == LogType.Warning
                && message == DxMessagingEditorInitializer.DomainReloadWarningMessage
            )
            {
                _warnings.Add(message);
            }
        }

        private void DisableDomainReload()
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions |= EnterPlayModeOptions.DisableDomainReload;
        }

        [Test]
        public void ApplyEditorSettingsUsesPassiveLegacyDiagnosticsValueImmediately()
        {
            DxMessagingSettings settings = NewSettings();
            settings._diagnosticsTargets = DiagnosticsTarget.Off;
            settings._legacyEnableDiagnosticsInEditor = true;
            settings._diagnosticsStackTraces = true;
            settings._messageBufferSize = 37;
            _settingsCache.SetCurrent(settings);
            DxMessagingEditorInitializer.ApplyEditorSettings();
            Assert.That(IMessageBus.GlobalDiagnosticsTargets, Is.EqualTo(DiagnosticsTarget.Editor));
            Assert.That(IMessageBus.GlobalDiagnosticsStackTraces, Is.True);
            Assert.That(IMessageBus.GlobalMessageBufferSize, Is.EqualTo(37));
            Assert.That(
                settings._legacyEnableDiagnosticsInEditor,
                Is.True,
                "The passive path must preserve the original asset."
            );
            Assert.That(_scheduled.Count, Is.EqualTo(1));
        }

        [Test]
        public void ApplyEditorSettingsWhenPassiveSettingsMissingDefersEnsureWithoutCreatingSynchronously()
        {
            _settingsCache.SetCurrent(null);
            IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.All;
            IMessageBus.GlobalDiagnosticsStackTraces = true;
            IMessageBus.GlobalMessageBufferSize = 3;
            DxMessagingEditorInitializer.ApplyEditorSettings();
            Assert.That(_scheduled.Count, Is.EqualTo(1));
            Assert.That(
                DxMessagingSettings.LoadSettingsPassive(),
                Is.Null,
                "Applying globals must not replace a cached miss by creating settings synchronously."
            );
            Assert.That(IMessageBus.GlobalDiagnosticsTargets, Is.EqualTo(DiagnosticsTarget.Off));
            Assert.That(IMessageBus.GlobalDiagnosticsStackTraces, Is.False);
            Assert.That(
                IMessageBus.GlobalMessageBufferSize,
                Is.EqualTo(IMessageBus.DefaultMessageBufferSize)
            );
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void ApplyEditorSettingsWarnsImmediatelyWhenPassiveSettingsUnsuppressedAndDomainReloadDisabled()
        {
            DxMessagingSettings settings = NewSettings();
            settings._suppressDomainReloadWarning = false;
            _settingsCache.SetCurrent(settings);
            DisableDomainReload();
            DxMessagingEditorInitializer.ApplyEditorSettings();
            Assert.That(
                _warnings,
                Is.EqualTo(new[] { DxMessagingEditorInitializer.DomainReloadWarningMessage })
            );
            Assert.That(_scheduled.Count, Is.EqualTo(1));
            _scheduled[0].Invoke();
            Assert.That(
                _warnings,
                Is.EqualTo(new[] { DxMessagingEditorInitializer.DomainReloadWarningMessage }),
                "The real deferred ensure must not repeat an issued warning."
            );
        }

        [Test]
        public void DeferredSettingsEnsureUsesRealIdleQueueAndClearsDebounce()
        {
            _settingsCache.SetCurrent(null);
            DxMessagingEditorInitializer.ApplyEditorSettings();
            DxMessagingEditorInitializer.ApplyEditorSettings();
            Assert.That(
                _scheduled.Count,
                Is.EqualTo(1),
                "Repeated requests must share one real queued ensure."
            );
            DxMessagingSettings settings = NewSettings();
            settings._diagnosticsTargets = DiagnosticsTarget.Runtime;
            settings._messageBufferSize = 19;
            _settingsCache.SetCurrent(settings);
            _scheduled[0].Invoke();
            Assert.That(
                IMessageBus.GlobalDiagnosticsTargets,
                Is.EqualTo(DiagnosticsTarget.Runtime)
            );
            Assert.That(IMessageBus.GlobalMessageBufferSize, Is.EqualTo(19));
            DxMessagingEditorInitializer.ApplyEditorSettings();
            Assert.That(_scheduled.Count, Is.EqualTo(2));
            _scheduled[1].Invoke();
            Assert.That(IMessageBus.GlobalMessageBufferSize, Is.EqualTo(19));
            MethodInfo scheduler = typeof(DxMessagingEditorInitializer).GetMethod(
                "ScheduleEnsureSettingsAsset",
                BindingFlags.Static | BindingFlags.NonPublic
            );
            Assert.That(
                EditorProductionCalls
                    .From(scheduler)
                    .Any(call =>
                        call.DeclaringType == typeof(DxMessagingEditorIdle)
                        && call.Name == "ScheduleAssetDatabaseMutation"
                    ),
                Is.True
            );
        }

        [Test]
        public void DeferredSettingsEnsureWarnsOnceWhenDomainReloadIsDisabledAndWarningUnsuppressed()
        {
            _settingsCache.SetCurrent(null);
            DisableDomainReload();
            DxMessagingEditorInitializer.ApplyEditorSettings();
            Assert.That(_warnings, Is.Empty);
            Assert.That(_scheduled.Count, Is.EqualTo(1));
            DxMessagingSettings settings = NewSettings();
            settings._suppressDomainReloadWarning = false;
            settings._diagnosticsTargets = DiagnosticsTarget.Runtime;
            settings._messageBufferSize = 23;
            _settingsCache.SetCurrent(settings);
            _scheduled[0].Invoke();
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
            Assert.That(
                _warnings,
                Is.EqualTo(new[] { DxMessagingEditorInitializer.DomainReloadWarningMessage })
            );
        }

        [Test]
        public void DeferredSettingsEnsureKeepsDefaultSuppressedDomainReloadWarningQuiet()
        {
            DxMessagingSettings settings = NewSettings();
            Assert.That(settings._suppressDomainReloadWarning, Is.True);
            _settingsCache.SetCurrent(settings);
            DisableDomainReload();
            DxMessagingEditorInitializer.ApplyEditorSettings();
            _scheduled[0].Invoke();
            Assert.That(_warnings, Is.Empty);
        }

        private DxMessagingSettings NewSettings()
        {
            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            _createdSettings.Add(settings);
            return settings;
        }
    }

    [TestFixture]
    public sealed class DxMessagingBaseCallIgnoreSyncTests
    {
        private readonly List<DxMessagingSettings> _createdSettings = new();
        private EditorDelayCallScope _scheduled;
        private EditorDelayCallScope _responseCallbacks;
        private EditorCompilerInputScope _inputs;
        private EditorSettingsCacheScope _settingsCache;
        private static string SidecarPath =>
            Path.GetFullPath(DxMessagingBaseCallIgnoreSync.SidecarAssetPath);

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (EditorApplication.isCompiling)
            {
                yield return new RecompileScripts();
            }
            _scheduled = new(typeof(DxMessagingBaseCallIgnoreSync));
            _responseCallbacks = new(typeof(SetupCscRsp));
            _inputs = new();
            _settingsCache = new();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try
            {
                _scheduled?.Dispose();
                _responseCallbacks?.Dispose();
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
                try
                {
                    _inputs?.Dispose();
                }
                finally
                {
                    _settingsCache?.Dispose();
                }
            }
            if (EditorApplication.isCompiling)
            {
                yield return new RecompileScripts();
            }
        }

        [Test]
        public void PreparedCompilerInputWritersRemainIdempotent()
        {
            string project = Path.Combine(Path.GetTempPath(), $"dxm_prepared_{Guid.NewGuid():N}");
            string assets = Path.Combine(project, "Assets");
            string sidecar = Path.Combine(project, DxMessagingBaseCallIgnoreSync.SidecarAssetPath);
            Directory.CreateDirectory(assets);
            try
            {
                DxMessagingSettings settings = NewSettings();
                settings._baseCallIgnoredTypes = new List<string> { "Consumer.IgnoredType" };
                bool sidecarPending = false;
                bool rspPending = false;
                List<string> imports = new();
                Action writeSidecar = () =>
                    DxMessagingBaseCallIgnoreSync.WriteSidecarOrThrow(
                        sidecar,
                        settings._baseCallIgnoredTypes,
                        imports.Add,
                        ref sidecarPending
                    );
                Action writeResponse = () =>
                    SetupCscRsp.SynchronizeResponseFiles(assets, imports.Add, ref rspPending);
                SetupCscRsp.PrepareCompilerInputs(
                    true,
                    writeSidecar,
                    writeResponse,
                    () => { },
                    () => { },
                    ref sidecarPending,
                    ref rspPending
                );
                DateTime unchangedTime = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(sidecar, unchangedTime);
                File.SetLastWriteTimeUtc(Path.Combine(assets, "csc.rsp"), unchangedTime);
                writeSidecar();
                writeResponse();
                CollectionAssert.AreEqual(
                    new[] { DxMessagingBaseCallIgnoreSync.SidecarAssetPath, "Assets/csc.rsp" },
                    imports,
                    "Reapplying the actual prepared writers must not import unchanged inputs."
                );
                Assert.AreEqual(unchangedTime, File.GetLastWriteTimeUtc(sidecar));
                Assert.AreEqual(
                    unchangedTime,
                    File.GetLastWriteTimeUtc(Path.Combine(assets, "csc.rsp"))
                );
            }
            finally
            {
                Directory.Delete(project, true);
            }
        }

        /// <remarks>
        /// 2026-10-18: importing a temporary sidecar requested compilation while NUnit locked
        /// reloads in Unity 2021 CI. The fixture batches imports until original inputs are
        /// restored, then lets the framework complete pending compilation before the next
        /// test constructs state. Unity setup is not rerun after that domain reload.
        /// </remarks>
        [Test]
        public void RealDeferredCallbacksDoNotRewritePreparedCompilerInputs()
        {
            DxMessagingSettings settings = NewSettings();
            _settingsCache.SetCurrent(settings);
            DxMessagingBaseCallIgnoreSync.RegenerateSidecarDeferred(settings);
            Assert.That(_scheduled.Count, Is.EqualTo(1));
            SetupCscRsp.PrepareCompilerInputs();
            Assert.That(
                DxMessagingEditorIdle.CanMutateAssetDatabase(),
                Is.True,
                "Temporary compiler inputs must remain batched until fixture restoration. "
                    + $"Compiling={EditorApplication.isCompiling}, updating={EditorApplication.isUpdating}."
            );
            byte[] sidecar = File.ReadAllBytes(SidecarPath);
            byte[] response = File.ReadAllBytes(Path.Combine(Application.dataPath, "csc.rsp"));
            DateTime sidecarTime = File.GetLastWriteTimeUtc(SidecarPath);
            DateTime responseTime = File.GetLastWriteTimeUtc(
                Path.Combine(Application.dataPath, "csc.rsp")
            );
            _scheduled[0].Invoke();
            Assert.That(_responseCallbacks.Count, Is.EqualTo(1));
            _responseCallbacks[0].Invoke();
            CollectionAssert.AreEqual(sidecar, File.ReadAllBytes(SidecarPath));
            CollectionAssert.AreEqual(
                response,
                File.ReadAllBytes(Path.Combine(Application.dataPath, "csc.rsp"))
            );
            Assert.That(File.GetLastWriteTimeUtc(SidecarPath), Is.EqualTo(sidecarTime));
            Assert.That(
                File.GetLastWriteTimeUtc(Path.Combine(Application.dataPath, "csc.rsp")),
                Is.EqualTo(responseTime)
            );
        }

        [Test]
        public void OnValidateDefersSidecarRegenerationInsteadOfImportingSynchronously()
        {
            DxMessagingSettings settings = NewSettings();
            byte[] before = CurrentSidecar();
            InvokePrivate(settings, "OnValidate");
            CollectionAssert.AreEqual(
                before,
                CurrentSidecar(),
                "OnValidate must not synchronously write or import compiler inputs."
            );
            Assert.That(_scheduled.Count, Is.EqualTo(1));
            _scheduled[0].Invoke();
            AssertContent(settings);
            Assert.That(_responseCallbacks.Count, Is.EqualTo(1));
        }

        [Test]
        public void OnEnableDoesNotRegenerateSidecar()
        {
            DxMessagingSettings settings = NewSettings();
            byte[] before = CurrentSidecar();
            InvokePrivate(settings, "OnEnable");
            CollectionAssert.AreEqual(before, CurrentSidecar());
            Assert.That(_scheduled.Count, Is.Zero);
            Assert.That(_responseCallbacks.Count, Is.Zero);
        }

        [Test]
        public void RegenerateSidecarDeferredNeverAppliesSynchronously()
        {
            DxMessagingSettings settings = NewSettings();
            byte[] before = CurrentSidecar();
            DxMessagingBaseCallIgnoreSync.RegenerateSidecarDeferred(settings);
            CollectionAssert.AreEqual(before, CurrentSidecar());
            Assert.That(_scheduled.Count, Is.EqualTo(1));
            _scheduled[0].Invoke();
            AssertContent(settings);
            Assert.That(_responseCallbacks.Count, Is.EqualTo(1));
        }

        [Test]
        public void DeferredSidecarWorkRemainsQueuedUntilSharedIdleSchedulerAllowsIt()
        {
            DxMessagingSettings settings = NewSettings();
            byte[] before = CurrentSidecar();
            DxMessagingBaseCallIgnoreSync.RegenerateSidecarDeferred(settings);
            Assert.That(_scheduled.Count, Is.EqualTo(1));
            bool idle = false;
            List<Action> ticks = new();
            DxMessagingEditorIdle.ScheduleAssetDatabaseMutation(
                _scheduled[0],
                ticks.Add,
                () => idle
            );
            ticks[0]();
            CollectionAssert.AreEqual(before, CurrentSidecar());
            Assert.That(ticks.Count, Is.EqualTo(2));
            idle = true;
            ticks[1]();
            AssertContent(settings);
            Assert.That(ticks.Count, Is.EqualTo(2));
            Assert.That(_responseCallbacks.Count, Is.EqualTo(1));
        }

        [Test]
        public void RegenerateSidecarAppliesSynchronouslyWhenEditorCanMutateAssets()
        {
            DxMessagingSettings settings = NewSettings();
            Assert.That(DxMessagingEditorIdle.CanMutateAssetDatabase(), Is.True);
            DxMessagingBaseCallIgnoreSync.RegenerateSidecar(settings);
            AssertContent(settings);
            Assert.That(_scheduled.Count, Is.Zero);
            Assert.That(_responseCallbacks.Count, Is.EqualTo(1));
        }

        [Test]
        public void SynchronousSidecarEntryRetainsItsRealBusyAndDeferredGuards()
        {
            MethodInfo entry = typeof(DxMessagingBaseCallIgnoreSync).GetMethod(
                nameof(DxMessagingBaseCallIgnoreSync.RegenerateSidecar)
            );
            MethodBase[] calls = EditorProductionCalls.From(entry).ToArray();
            Assert.That(
                calls.Any(call =>
                    call.DeclaringType == typeof(DxMessagingEditorIdle)
                    && call.Name == "CanMutateAssetDatabase"
                ),
                Is.True
            );
            Assert.That(
                calls.Any(call =>
                    call.DeclaringType == typeof(DxMessagingBaseCallIgnoreSync)
                    && call.Name == "RegenerateSidecarDeferred"
                ),
                Is.True
            );
            Assert.That(
                calls.Any(call =>
                    call.DeclaringType == typeof(DxMessagingBaseCallIgnoreSync)
                    && call.Name == "ApplySidecarAndScheduleCscRspSync"
                ),
                Is.True
            );
        }

        [Test]
        public void RegenerateSidecarDeferredSkipsDestroyedSettings()
        {
            DxMessagingSettings settings = NewSettings();
            byte[] before = CurrentSidecar();
            DxMessagingBaseCallIgnoreSync.RegenerateSidecarDeferred(settings);
            Assert.That(_scheduled.Count, Is.EqualTo(1));
            Object.DestroyImmediate(settings);
            _createdSettings.Remove(settings);
            Assert.DoesNotThrow(() => _scheduled[0].Invoke());
            CollectionAssert.AreEqual(before, CurrentSidecar());
            Assert.That(_responseCallbacks.Count, Is.Zero);
        }

        private DxMessagingSettings NewSettings()
        {
            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            _createdSettings.Add(settings);
            _scheduled.Clear();
            _responseCallbacks.Clear();
            settings._baseCallIgnoredTypes = new List<string>
            {
                "Owned.SidecarProbe" + Guid.NewGuid().ToString("N"),
            };
            return settings;
        }

        private static byte[] CurrentSidecar() =>
            File.Exists(SidecarPath) ? File.ReadAllBytes(SidecarPath) : null;

        private static void AssertContent(DxMessagingSettings settings) =>
            Assert.That(
                File.ReadAllText(SidecarPath),
                Is.EqualTo(
                    DxMessagingBaseCallIgnoreSync.BuildContent(settings._baseCallIgnoredTypes)
                )
            );

        private static void InvokePrivate(DxMessagingSettings settings, string methodName)
        {
            MethodInfo method = typeof(DxMessagingSettings).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(method, Is.Not.Null);
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

            /*
                Pin the exact wire format the analyzer reads: LF-separated lines, one trailing LF, no
                blank lines (the lenient SplitLines helper would otherwise mask a separator regression).
            */
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
