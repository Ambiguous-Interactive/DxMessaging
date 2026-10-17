#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Analyzers;
    using DxMessaging.Editor.Settings;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.TestTools;
    using Object = UnityEngine.Object;

    [TestFixture]
    public sealed class DxMessagingSettingsLookupTests
    {
        private const string DefaultPath = "Assets/Editor/DxMessagingSettings.asset";
        private const string LegacyPath = "Assets/Legacy/Settings.asset";
        private readonly Dictionary<string, DxMessagingSettings> _assets = new();
        private readonly Dictionary<string, string> _guidPaths = new();
        private readonly List<Object> _createdObjects = new();
        private string[] _guids;
        private int _loads;
        private int _searches;
        private int _resolutions;

        [SetUp]
        public void SetUp()
        {
            DxMessagingSettings.InvalidateSettingsCache();
            _assets.Clear();
            _guidPaths.Clear();
            _guids = Array.Empty<string>();
            _loads = 0;
            _searches = 0;
            _resolutions = 0;
        }

        [TearDown]
        public void TearDown()
        {
            DxMessagingSettings.InvalidateSettingsCache();
            foreach (Object instance in _createdObjects)
            {
                if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }
            }
            _createdObjects.Clear();
        }

        /// <remarks>
        /// Issue #613: the harvester searched all project assets on every rescan, including
        /// when settings were absent. A completed miss must remain cached until invalidated.
        /// </remarks>
        [Test]
        public void RepeatedAbsentSettingsReadsPerformOneSearchAndNoFurtherLoads()
        {
            for (int i = 0; i < 64; i++)
            {
                Assert.That(Read() == null, Is.True, "Absent settings must remain absent.");
            }
            Assert.That(_searches, Is.EqualTo(1), "A stable miss must query exactly once.");
            Assert.That(_loads, Is.EqualTo(1), "A cached miss must not reload the default path.");
            Assert.That(_resolutions, Is.Zero, "An empty search has no GUIDs to resolve.");
        }

        [Test]
        public void DefaultSettingsArePreferredAndRemainCachedWithoutSearching()
        {
            DxMessagingSettings settings = CreateSettings(DefaultPath);
            AddLegacySettings();
            for (int i = 0; i < 64; i++)
            {
                Assert.That(
                    Read(),
                    Is.SameAs(settings),
                    "The canonical asset must take precedence."
                );
            }
            Assert.That(_loads, Is.EqualTo(1), "An unchanged live asset must load once.");
            Assert.That(_searches, Is.Zero, "The canonical asset requires no project-wide search.");
        }

        [Test]
        public void LegacySettingsAreResolvedOnceAndReturnLiveModifiedValues()
        {
            DxMessagingSettings settings = AddLegacySettings();
            Assert.That(Read(), Is.SameAs(settings), "Legacy settings must remain discoverable.");
            settings._baseCallCheckEnabled = false;
            settings._useConsoleBridge = true;
            DxMessagingSettings current = Read();
            Assert.That(
                current._baseCallCheckEnabled,
                Is.False,
                "Cached assets retain live edits."
            );
            Assert.That(
                current._useConsoleBridge,
                Is.True,
                "Bridge edits must be visible immediately."
            );
            Assert.That(
                _searches,
                Is.EqualTo(1),
                "Live reads must not repeat the fallback search."
            );
            Assert.That(_loads, Is.EqualTo(2), "Only canonical and legacy paths should be loaded.");
        }

        /// <remarks>
        /// 2026-10-07, issue #613: CI can start compilation before EditMode tests lock
        /// assembly reloads. Ordinary yields cannot finish that pending reload. Let the
        /// framework complete compilation and restore the test before constructing settings.
        /// RescanNow retains its busy guard, and both calls execute in the same idle update.
        /// </remarks>
        [UnityTest]
        [Category("Integration")]
        public IEnumerator HarvesterRescansReuseTheSharedPassiveSettingsCache()
        {
            if (EditorApplication.isCompiling)
            {
                yield return new WaitForDomainReload();
            }
            Assert.That(
                DxMessagingEditorIdle.CanMutateAssetDatabase(),
                Is.True,
                "Compilation must finish before the guarded rescan calls. "
                    + $"Compiling={EditorApplication.isCompiling}, updating={EditorApplication.isUpdating}."
            );
            DxMessagingSettings settings = CreateSettings(DefaultPath);
            settings._baseCallCheckEnabled = false;
            DxMessagingSettings.InvalidateSettingsCache();
            Read();
            int rescans = DxMessagingConsoleHarvester.RescanCount;
            int lookups = DxMessagingSettings.SettingsLookupCount;
            DxMessagingConsoleHarvester.RescanNow();
            DxMessagingConsoleHarvester.RescanNow();
            Assert.That(
                DxMessagingConsoleHarvester.RescanCount,
                Is.EqualTo(rescans + 2),
                "Both idle rescan calls must execute the production settings path."
            );
            Assert.That(
                DxMessagingSettings.SettingsLookupCount,
                Is.EqualTo(lookups + 2),
                "Harvester rescans must use the shared passive loader."
            );
            Assert.That(
                _loads,
                Is.EqualTo(1),
                "The harvester must reuse the already-loaded asset."
            );
            Assert.That(_searches, Is.Zero, "The harvester must not add a project-wide search.");
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void MasterCheckChangesDeferOneRescanAndUnchangedValuesDoNot(
            bool initialValue,
            bool changedValue
        )
        {
            DxMessagingSettings settings = CreateSettings(DefaultPath);
            settings._baseCallCheckEnabled = initialValue;
            int callbacks = EditorApplication.delayCall?.GetInvocationList().Length ?? 0;
            try
            {
                settings.BaseCallCheckEnabled = changedValue;
                Assert.That(
                    EditorApplication.delayCall?.GetInvocationList().Length ?? 0,
                    Is.EqualTo(callbacks + 1),
                    "Enabling or disabling checks must defer a rescan without running it synchronously."
                );
                settings.BaseCallCheckEnabled = changedValue;
                Assert.That(
                    EditorApplication.delayCall?.GetInvocationList().Length ?? 0,
                    Is.EqualTo(callbacks + 1),
                    "Assigning an unchanged value must not add another callback."
                );
            }
            finally
            {
                EditorApplication.delayCall -= DxMessagingConsoleHarvester.RequestRescan;
            }
        }

        [TestCase("import")]
        [TestCase("delete")]
        [TestCase("move")]
        [TestCase("move-from")]
        public void AssetNotificationsInvalidateWithoutPerformingSettingsLookup(string channel)
        {
            Assert.That(Read() == null, Is.True, "Start from a cached miss.");
            DxMessagingSettings settings = AddLegacySettings();
            int revision = DxMessagingSettings.SettingsRevision;
            string[] changed = { LegacyPath };
            string[] empty = Array.Empty<string>();
            DxMessagingSettings.NotifyAssetsChanged(
                channel == "import" ? changed : empty,
                channel == "delete" ? changed : empty,
                channel == "move" ? changed : empty,
                channel == "move-from" ? changed : empty
            );
            Assert.That(
                DxMessagingSettings.SettingsRevision,
                Is.Not.EqualTo(revision),
                "Every settings asset-change channel must invalidate the cached revision."
            );
            Assert.That(_searches, Is.EqualTo(1), "Import notification must not query settings.");
            Assert.That(_loads, Is.EqualTo(1), "Import notification must not load settings.");
            Assert.That(Read(), Is.SameAs(settings), "The next read must discover the new asset.");
            Assert.That(
                _searches,
                Is.EqualTo(2),
                "The next revision permits exactly one new search."
            );
        }

        [Test]
        public void EmptyAndUnrelatedNotificationsPreserveCachedAbsence()
        {
            Read();
            int revision = DxMessagingSettings.SettingsRevision;
            DxMessagingSettings.NotifyAssetsChanged(null, null, null, null);
            DxMessagingSettings.NotifyAssetsChanged(
                new[] { "Assets/Unrelated.cs", "Assets/Unrelated", "", null },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()
            );
            Assert.That(
                DxMessagingSettings.SettingsRevision,
                Is.EqualTo(revision),
                "Empty paths and unrelated source/folder changes must not invalidate settings."
            );
            Read();
            Assert.That(_searches, Is.EqualTo(1), "Unrelated changes must not amplify searches.");
        }

        [Test]
        public void ParentFolderMovesInvalidateTheSelectedLegacyPath()
        {
            DxMessagingSettings settings = AddLegacySettings();
            Read();
            _assets.Remove(LegacyPath);
            const string newPath = "Assets/Renamed/Settings.asset";
            _assets[newPath] = settings;
            _guidPaths["legacy"] = newPath;
            DxMessagingSettings.NotifyAssetsChanged(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { "Assets/Renamed" },
                new[] { "Assets/Legacy" }
            );
            Assert.That(
                Read(),
                Is.SameAs(settings),
                "Folder moves must re-resolve the same asset."
            );
            Assert.That(_searches, Is.EqualTo(2), "A folder move must invalidate the legacy path.");
        }

        [Test]
        public void DeletedSettingsBecomeACachedMiss()
        {
            AddLegacySettings();
            Read();
            _assets.Remove(LegacyPath);
            _guids = Array.Empty<string>();
            DxMessagingSettings.NotifyAssetsChanged(null, new[] { LegacyPath }, null, null);
            Assert.That(
                Read() == null,
                Is.True,
                "A deleted setting must not retain its live reference."
            );
            Assert.That(Read() == null, Is.True, "The new miss must be cached.");
            Assert.That(_searches, Is.EqualTo(2), "Deletion must produce one replacement lookup.");
        }

        [Test]
        public void DestroyedUnityObjectIsReloadedWithoutAnAssetNotification()
        {
            DxMessagingSettings settings = AddLegacySettings();
            Read();
            Object.DestroyImmediate(settings);
            DxMessagingSettings replacement = AddLegacySettings();
            Assert.That(
                Read(),
                Is.SameAs(replacement),
                "Unity's destroyed-object null must trigger a new lookup, not a cached miss."
            );
            Assert.That(_searches, Is.EqualTo(2), "Destroyed objects must be resolved again.");
        }

        [Test]
        public void InvalidGuidPathsAndNullAssetsDoNotHideALaterValidSettingsAsset()
        {
            DxMessagingSettings settings = AddLegacySettings();
            _guids = new[] { "missing-path", "missing-asset", "legacy" };
            _guidPaths["missing-asset"] = "Assets/Missing.asset";
            Assert.That(
                Read(),
                Is.SameAs(settings),
                "Invalid early GUIDs must not hide a valid asset."
            );
            Assert.That(
                _resolutions,
                Is.EqualTo(3),
                "Every earlier invalid entry must be examined."
            );
            Assert.That(_loads, Is.EqualTo(3), "Do not load an empty GUID path.");
        }

        [Test]
        public void FailedSearchDoesNotPublishACompletedMiss()
        {
            Assert.Throws<InvalidOperationException>(
                () =>
                    DxMessagingSettings.LoadSettingsPassive(
                        Load,
                        () => throw new InvalidOperationException("injected search failure"),
                        Resolve
                    ),
                "The injected lookup failure must escape before publishing a cache entry."
            );
            DxMessagingSettings settings = AddLegacySettings();
            Assert.That(Read(), Is.SameAs(settings), "A failed search must remain retryable.");
            Assert.That(_searches, Is.EqualTo(1), "The successful retry must execute its search.");
        }

        [Test]
        public void InvalidationDuringLoadingRequiresAnotherCompletedLookup()
        {
            DxMessagingSettings settings = CreateSettings(DefaultPath);
            DxMessagingSettings first = DxMessagingSettings.LoadSettingsPassive(
                path =>
                {
                    DxMessagingSettings.InvalidateSettingsCache();
                    return Load(path);
                },
                Search,
                Resolve
            );
            Assert.That(first, Is.SameAs(settings), "The current load may return the live object.");
            Assert.That(Read(), Is.SameAs(settings), "The invalidated revision must reload.");
            Assert.That(
                _loads,
                Is.EqualTo(2),
                "An in-flight invalidation must not be overwritten."
            );
        }

        [Test]
        public void ExplicitInvalidationRefreshesCachedAbsence()
        {
            Read();
            DxMessagingSettings settings = CreateSettings(DefaultPath);
            DxMessagingSettings.InvalidateSettingsCache();
            Assert.That(
                Read(),
                Is.SameAs(settings),
                "A fresh revision must discover canonical settings."
            );
            Assert.That(_loads, Is.EqualTo(2), "A reset revision requires a fresh canonical load.");
        }

        private DxMessagingSettings Read()
        {
            return DxMessagingSettings.LoadSettingsPassive(Load, Search, Resolve);
        }

        private DxMessagingSettings Load(string path)
        {
            _loads++;
            return _assets.TryGetValue(path, out DxMessagingSettings settings) ? settings : null;
        }

        private string[] Search()
        {
            _searches++;
            return _guids;
        }

        private string Resolve(string guid)
        {
            _resolutions++;
            return _guidPaths.TryGetValue(guid, out string path) ? path : null;
        }

        private DxMessagingSettings AddLegacySettings()
        {
            _guids = new[] { "legacy" };
            _guidPaths["legacy"] = LegacyPath;
            return CreateSettings(LegacyPath);
        }

        private DxMessagingSettings CreateSettings(string path)
        {
            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            _createdObjects.Add(settings);
            _assets[path] = settings;
            return settings;
        }
    }
}
#endif
