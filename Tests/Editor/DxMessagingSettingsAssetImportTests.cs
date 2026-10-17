#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using DxMessaging.Editor.Settings;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    [TestFixture]
    public sealed class DxMessagingSettingsAssetImportTests
    {
        private readonly List<Object> _createdObjects = new();
        private readonly List<Action> _deferredSidecarWork = new();
        private Action<Action> _originalSidecarScheduler;
        private string _folder;
        private string _assetPath;

        [SetUp]
        public void SetUp()
        {
            _originalSidecarScheduler = DxMessagingBaseCallIgnoreSync.DeferralScheduler;
            DxMessagingBaseCallIgnoreSync.DeferralScheduler = _deferredSidecarWork.Add;
            string name = "__DxMessagingSettingsImportTests_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            Assert.That(Directory.Exists(_folder), Is.False, "The scratch folder must be new.");
            Assert.That(
                AssetDatabase.CreateFolder("Assets", name),
                Is.Not.Empty,
                "Create only the owned scratch folder."
            );
            _assetPath = _folder + "/Settings.asset";
            DxMessagingSettings.InvalidateSettingsCache();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (!string.IsNullOrEmpty(_folder) && AssetDatabase.IsValidFolder(_folder))
                {
                    Assert.That(
                        AssetDatabase.DeleteAsset(_folder),
                        Is.True,
                        "Delete only this test's owned scratch folder."
                    );
                }
                foreach (Object instance in _createdObjects)
                {
                    if (instance != null && !EditorUtility.IsPersistent(instance))
                    {
                        Object.DestroyImmediate(instance);
                    }
                }
            }
            finally
            {
                _createdObjects.Clear();
                _deferredSidecarWork.Clear();
                DxMessagingBaseCallIgnoreSync.DeferralScheduler = _originalSidecarScheduler;
                DxMessagingSettings.InvalidateSettingsCache();
                _folder = null;
            }
        }

        [Test]
        public void ImportedSettingsReplaceACachedMissWithoutDomainReload()
        {
            DxMessagingSettings settings = CreateTransientSettings();
            DxMessagingSettings.InvalidateSettingsCache();
            Assert.That(Read() == null, Is.True, "The owned folder starts with a cached miss.");
            int searches = DxMessagingSettings.SettingsSearchCount;
            AssetDatabase.CreateAsset(settings, _assetPath);
            AssetDatabase.ImportAsset(_assetPath, ImportAssetOptions.ForceSynchronousImport);

            Assert.That(
                DxMessagingSettings.SettingsSearchCount,
                Is.EqualTo(searches),
                "Native import callbacks must not search for settings synchronously."
            );
            Assert.That(Read(), Is.SameAs(settings), "An import must invalidate cached absence.");
            Assert.That(
                DxMessagingSettings.SettingsSearchCount,
                Is.EqualTo(searches + 1),
                "The first passive read after import performs one replacement search."
            );
        }

        [TestCase("delete")]
        [TestCase("asset-move")]
        [TestCase("folder-move")]
        public void NativeAssetChangesInvalidateTheSelectedSettingsPath(string change)
        {
            DxMessagingSettings settings = CreateTransientSettings();
            AssetDatabase.CreateAsset(settings, _assetPath);
            AssetDatabase.ImportAsset(_assetPath, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(Read(), Is.SameAs(settings), "Start with the owned imported settings.");
            int revision = DxMessagingSettings.SettingsRevision;
            int searches = DxMessagingSettings.SettingsSearchCount;

            if (change == "delete")
            {
                Assert.That(
                    AssetDatabase.DeleteAsset(_assetPath),
                    Is.True,
                    "Delete owned settings."
                );
            }
            else if (change == "asset-move")
            {
                string destination = _folder + "/Renamed.asset";
                Assert.That(
                    AssetDatabase.MoveAsset(_assetPath, destination),
                    Is.Empty,
                    "Move the owned settings asset."
                );
                _assetPath = destination;
            }
            else
            {
                string destination = _folder + "_Moved";
                Assert.That(
                    AssetDatabase.MoveAsset(_folder, destination),
                    Is.Empty,
                    "Move only the owned parent folder."
                );
                _folder = destination;
                _assetPath = _folder + "/Settings.asset";
            }

            Assert.That(
                DxMessagingSettings.SettingsRevision,
                Is.Not.EqualTo(revision),
                $"The native {change} callback must invalidate the selected path."
            );
            Assert.That(
                DxMessagingSettings.SettingsSearchCount,
                Is.EqualTo(searches),
                $"The native {change} callback must defer settings lookup."
            );
            DxMessagingSettings current = Read();
            Assert.That(
                current == null,
                Is.EqualTo(change == "delete"),
                $"The next passive read must reflect native {change}."
            );
            Assert.That(
                Read(),
                Is.SameAs(current),
                $"The resolved native {change} result must remain cached."
            );
            Assert.That(
                DxMessagingSettings.SettingsSearchCount,
                Is.EqualTo(searches + 1),
                $"Native {change} must permit exactly one replacement search."
            );
        }

        private DxMessagingSettings CreateTransientSettings()
        {
            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            _createdObjects.Add(settings);
            return settings;
        }

        private DxMessagingSettings Read()
        {
            /*
                Keep real AssetDatabase loads/searches inside the owned folder. Existing user
                settings must not be removed or selected to manufacture an absent environment.
            */
            return DxMessagingSettings.LoadSettingsPassive(
                path =>
                    path.StartsWith(_folder + "/", StringComparison.Ordinal)
                        ? AssetDatabase.LoadAssetAtPath<DxMessagingSettings>(path)
                        : null,
                () => AssetDatabase.FindAssets("t:DxMessagingSettings", new[] { _folder }),
                AssetDatabase.GUIDToAssetPath
            );
        }
    }
}
#endif
