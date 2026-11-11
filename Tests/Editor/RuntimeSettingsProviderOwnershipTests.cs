#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>Resets release provider-created native settings without consuming external ownership.</summary>
    /// <remarks>
    /// 2026-11-10: clearing the cache abandoned HideAndDontSave fallback objects. Preserve the
    /// host's original cache and pending validation queue while testing actual reset behavior.
    /// </remarks>
    [Category("Contract")]
    public sealed class RuntimeSettingsProviderOwnershipTests
    {
        private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly Type Provider = typeof(DxMessagingRuntimeSettingsProvider);
        private static readonly FieldInfo Cache = Provider.GetField("_cached", StaticFields);
        private static readonly FieldInfo Owned = Provider.GetField("_ownedFallback", StaticFields);
        private readonly List<DxMessagingRuntimeSettings> _created = new();
        private readonly List<DxMessagingRuntimeSettings> _savedPending = new();
        private ConcurrentQueue<DxMessagingRuntimeSettings> _pending;
        private object _savedCache;
        private object _savedOwned;
        private object _ownership;
        private FieldInfo _retainedFallback;
        private object _savedRetained;
        private bool _stateCaptured;
        private string _assetRoot;

        /// <summary>Isolates provider ownership without resetting existing host objects or buses.</summary>
        [SetUp]
        public void PreserveProviderState()
        {
            Assert.That(Application.isPlaying, Is.False, "Editor reset controls require EditMode.");
            Assert.That(Cache, Is.Not.Null, "The actual provider cache must be available.");
            Assert.That(
                Resources.Load<DxMessagingRuntimeSettings>(DxMessagingRuntimeSettings.ResourceName)
                    == null,
                Is.True,
                "Fallback creation requires the isolated test host's absent resource asset."
            );
            _savedCache = Cache.GetValue(null);
            _savedOwned = Owned?.GetValue(null);
            Type ownershipType = Provider.Assembly.GetType(
                "DxMessaging.Core.Configuration.DxMessagingRuntimeSettingsOwnership"
            );
            if (ownershipType != null)
            {
                _ownership = ownershipType
                    .GetProperty(
                        "instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy
                    )
                    .GetValue(null);
                _retainedFallback = ownershipType.GetField(
                    "_fallback",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
                _savedRetained = _retainedFallback.GetValue(_ownership);
            }
            _stateCaptured = true;
            Cache.SetValue(null, null);
            Owned?.SetValue(null, null);
            _retainedFallback?.SetValue(_ownership, null);
            _pending =
                (ConcurrentQueue<DxMessagingRuntimeSettings>)
                    typeof(DxMessagingRuntimeSettings)
                        .GetField("PendingValidations", StaticFields)
                        .GetValue(null);
            while (_pending.TryDequeue(out DxMessagingRuntimeSettings settings))
            {
                _savedPending.Add(settings);
            }
        }

        /// <summary>Destroys only test-created native objects and restores the exact saved state.</summary>
        [TearDown]
        public void RestoreProviderState()
        {
            if (!_stateCaptured)
            {
                return;
            }
            try
            {
                if (!string.IsNullOrEmpty(_assetRoot))
                {
                    AssetDatabase.DeleteAsset(_assetRoot);
                }
                foreach (DxMessagingRuntimeSettings settings in _created)
                {
                    if (settings != null)
                    {
                        Object.DestroyImmediate(settings);
                    }
                }
                if (_pending != null)
                {
                    List<DxMessagingRuntimeSettings> otherPending = new();
                    while (_pending.TryDequeue(out DxMessagingRuntimeSettings settings))
                    {
                        if (!_created.Exists(created => ReferenceEquals(created, settings)))
                        {
                            otherPending.Add(settings);
                        }
                    }
                    foreach (DxMessagingRuntimeSettings settings in _savedPending)
                    {
                        _pending.Enqueue(settings);
                    }
                    foreach (DxMessagingRuntimeSettings settings in otherPending)
                    {
                        _pending.Enqueue(settings);
                    }
                }
            }
            finally
            {
                Cache?.SetValue(null, _savedCache);
                Owned?.SetValue(null, _savedOwned);
                _retainedFallback?.SetValue(_ownership, _savedRetained);
                _created.Clear();
                _savedPending.Clear();
                _pending = null;
                _stateCaptured = false;
                _assetRoot = null;
            }
        }

        /// <summary>The real Resources load path borrows its asset and never destroys it on reset.</summary>
        [Test]
        public void ResetPreservesTheLoadedResourceAsset()
        {
            string folder = "DxmSettingsOwnership" + Guid.NewGuid().ToString("N");
            _assetRoot = "Assets/" + folder;
            AssetDatabase.CreateFolder("Assets", folder);
            AssetDatabase.CreateFolder(_assetRoot, "Resources");
            DxMessagingRuntimeSettings asset = CreateSettings();
            string path =
                _assetRoot + "/Resources/" + DxMessagingRuntimeSettings.ResourceName + ".asset";
            AssetDatabase.CreateAsset(asset, path);
            Assert.That(
                DxMessagingRuntimeSettingsProvider.Current,
                Is.SameAs(asset),
                "The actual Resources path must return the persistent settings asset."
            );
            DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
            Assert.That(asset != null, Is.True, "Reset must preserve the borrowed resource asset.");
            Assert.That(
                AssetDatabase.LoadAssetAtPath<DxMessagingRuntimeSettings>(path),
                Is.SameAs(asset),
                "The resource asset must remain stored and loadable after reset."
            );
        }

        /// <summary>Every reset destroys its own fallback, including repeated reset/read cycles.</summary>
        [Test]
        public void RepeatedResetsReleaseEveryProviderCreatedFallback()
        {
            List<int> survivors = new();
            for (int cycle = 0; cycle < 3; ++cycle)
            {
                DxMessagingRuntimeSettings settings = ReadFallback();
                int id = InstanceId.StableId(settings);
                DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
                Assert.That(
                    Cache.GetValue(null),
                    Is.Null,
                    "Reset must detach the cached reference."
                );
                if (settings != null)
                {
                    survivors.Add(id);
                }
            }
            Assert.That(
                survivors,
                Is.Empty,
                "Each provider-created fallback must be destroyed after its EditMode reset."
            );
        }

        /// <summary>A marker on a caller-created object does not transfer native ownership.</summary>
        [TestCase(false)]
        [TestCase(true)]
        public void ResetPreservesExternallyOwnedCachedSettings(bool markedFallback)
        {
            DxMessagingRuntimeSettings external = CreateSettings();
            if (markedFallback)
            {
                external.MarkAsFallbackInstance();
            }
            Cache.SetValue(null, external);
            DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
            Assert.That(
                external != null,
                Is.True,
                "Reset must preserve caller-owned native settings."
            );
            Assert.That(
                Cache.GetValue(null),
                Is.Null,
                "External cache entries must still be invalidated."
            );
        }

        /// <summary>Replacing the cache cannot hide a provider-owned object from its cleanup.</summary>
        [Test]
        public void ResetReleasesDetachedOwnedFallbackAndPreservesReplacement()
        {
            DxMessagingRuntimeSettings fallback = ReadFallback();
            DxMessagingRuntimeSettings external = CreateSettings();
            Cache.SetValue(null, external);
            DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
            Assert.That(
                fallback == null,
                Is.True,
                "The detached provider-owned fallback must be destroyed."
            );
            Assert.That(
                external != null,
                Is.True,
                "An external cache replacement must remain alive."
            );
        }

        /// <summary>A destroyed owned fallback can be reset and replaced without consuming another object.</summary>
        [Test]
        public void ResetAfterExternalDestructionAllowsFreshFallback()
        {
            DxMessagingRuntimeSettings first = ReadFallback();
            Object.DestroyImmediate(first);
            Assert.DoesNotThrow(
                DxMessagingRuntimeSettingsProvider.ResetCachedSettings,
                "An already destroyed owned object must not cause a second-destroy failure."
            );
            DxMessagingRuntimeSettings replacement = ReadFallback();
            Assert.That(
                replacement,
                Is.Not.SameAs(first),
                "The next read must create a new live fallback."
            );
            DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
            Assert.That(
                replacement == null,
                Is.True,
                "The replacement must obey the same reset lifetime."
            );
        }

        /// <summary>Empty resets are safe before creation and after successful cleanup.</summary>
        [Test]
        public void RepeatedEmptyResetsRemainIdempotent()
        {
            for (int cycle = 0; cycle < 3; ++cycle)
            {
                Assert.DoesNotThrow(
                    DxMessagingRuntimeSettingsProvider.ResetCachedSettings,
                    "Repeated empty reset must remain a no-op."
                );
                Assert.That(
                    Cache.GetValue(null),
                    Is.Null,
                    "An empty reset must not create settings."
                );
            }
        }

        /// <summary>Reload cleanup releases owned defaults while preserving a borrowed cache replacement.</summary>
        [Test]
        public void ReloadCleanupReleasesOwnedFallbackAndPreservesReplacement()
        {
            DxMessagingRuntimeSettings fallback = ReadFallback();
            DxMessagingRuntimeSettings external = CreateSettings();
            Cache.SetValue(null, external);
            MethodInfo cleanup = Provider.GetMethod("ReleaseBeforeAssemblyReload", StaticFields);
            Assert.That(
                cleanup,
                Is.Not.Null,
                "The provider needs an Editor reload cleanup boundary."
            );
            MethodInfo register = Provider.GetMethod("RegisterReloadCleanup", StaticFields);
            Assert.That(register, Is.Not.Null, "The provider must register its reload cleanup.");
            Assert.That(
                register.IsDefined(typeof(InitializeOnLoadMethodAttribute), false),
                Is.True,
                "Reload cleanup must be registered after every domain load."
            );
            cleanup.Invoke(null, null);
            Assert.That(
                fallback == null,
                Is.True,
                "Reload cleanup must release its own native fallback."
            );
            Assert.That(
                external != null,
                Is.True,
                "Reload cleanup must preserve the borrowed replacement."
            );
            Assert.That(Cache.GetValue(null), Is.Null, "Reload cleanup must detach cached state.");
        }

        /// <summary>Current recovers the exact owned fallback when reload discards only static references.</summary>
        [Test]
        public void CurrentRecoversRetainedOwnershipAfterStaticLoss()
        {
            DxMessagingRuntimeSettings retained = ReadFallback();
            Cache.SetValue(null, null);
            Owned?.SetValue(null, null);
            DxMessagingRuntimeSettings recovered = ReadFallback();
            Assert.That(
                recovered,
                Is.SameAs(retained),
                "Static loss must not abandon a retained owned fallback."
            );
            DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
            Assert.That(
                retained == null,
                Is.True,
                "The recovered fallback must remain releasable by reset."
            );
        }

        /// <summary>Loading a real resource after static loss does not abandon retained fallback ownership.</summary>
        [Test]
        public void ResourceAdoptionStillReleasesRetainedOwnedFallback()
        {
            DxMessagingRuntimeSettings retained = ReadFallback();
            Cache.SetValue(null, null);
            Owned?.SetValue(null, null);
            DxMessagingRuntimeSettings asset = CreateResourceAsset();
            Assert.That(
                DxMessagingRuntimeSettingsProvider.Current,
                Is.SameAs(asset),
                "Resources must take precedence."
            );
            DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
            Assert.That(
                retained == null,
                Is.True,
                "Reset must release retained ownership after resource adoption."
            );
            Assert.That(asset != null, Is.True, "Reset must preserve the borrowed resource asset.");
        }

        /// <summary>Reset releases both distinct proven ownership references without consuming an external cache.</summary>
        [Test]
        public void ResetReleasesDistinctStaticAndRetainedOwnedFallbacks()
        {
            Assert.That(
                _retainedFallback,
                Is.Not.Null,
                "Editor ownership must survive static loss."
            );
            DxMessagingRuntimeSettings first = ReadFallback();
            Cache.SetValue(null, null);
            Owned.SetValue(null, null);
            _retainedFallback.SetValue(_ownership, null);
            DxMessagingRuntimeSettings second = ReadFallback();
            _retainedFallback.SetValue(_ownership, first);
            DxMessagingRuntimeSettings external = CreateSettings();
            Cache.SetValue(null, external);
            DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
            Assert.That(first == null, Is.True, "Reset must release the retained owned object.");
            Assert.That(
                second == null,
                Is.True,
                "Reset must release the distinct static owned object."
            );
            Assert.That(
                external != null,
                Is.True,
                "Reset must preserve the external cached object."
            );
            Assert.That(
                _retainedFallback.GetValue(_ownership),
                Is.Null,
                "Reset must detach retained ownership."
            );
        }

        private DxMessagingRuntimeSettings CreateResourceAsset()
        {
            string folder = "DxmSettingsOwnership" + Guid.NewGuid().ToString("N");
            _assetRoot = "Assets/" + folder;
            AssetDatabase.CreateFolder("Assets", folder);
            AssetDatabase.CreateFolder(_assetRoot, "Resources");
            DxMessagingRuntimeSettings asset = CreateSettings();
            AssetDatabase.CreateAsset(
                asset,
                _assetRoot + "/Resources/" + DxMessagingRuntimeSettings.ResourceName + ".asset"
            );
            return asset;
        }

        private DxMessagingRuntimeSettings ReadFallback()
        {
            DxMessagingRuntimeSettings settings = DxMessagingRuntimeSettingsProvider.Current;
            _created.Add(settings);
            Assert.That(
                settings != null,
                Is.True,
                "The actual provider must create a live fallback."
            );
            Assert.That(
                settings.IsFallbackInstance,
                Is.True,
                "The created object must carry fallback identity."
            );
            Assert.That(
                settings.hideFlags,
                Is.EqualTo(HideFlags.HideAndDontSave),
                "Fallback visibility and persistence flags must remain unchanged."
            );
            return settings;
        }

        private DxMessagingRuntimeSettings CreateSettings()
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            _created.Add(settings);
            return settings;
        }
    }
}
#endif
