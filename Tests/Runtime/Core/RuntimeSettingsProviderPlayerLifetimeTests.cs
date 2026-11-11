#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core.Configuration;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;
    using Object = UnityEngine.Object;

    /// <summary>Provider fallback cleanup follows the player's deferred native-object lifetime.</summary>
    /// <remarks>
    /// 2026-11-10: clearing a provider cache left HideAndDontSave defaults alive. Keep the real
    /// end-of-frame destruction boundary separate from immediate Editor destruction.
    /// </remarks>
    [Category("Contract")]
    public sealed class RuntimeSettingsProviderPlayerLifetimeTests
    {
        private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly Type Provider = typeof(DxMessagingRuntimeSettingsProvider);
        private static readonly FieldInfo Cache = Provider.GetField("_cached", StaticFields);
        private static readonly FieldInfo Owned = Provider.GetField("_ownedFallback", StaticFields);
        private readonly List<DxMessagingRuntimeSettings> _created = new();
        private object _savedCache;
        private object _savedOwned;
        private object _ownership;
        private FieldInfo _retainedFallback;
        private object _savedRetained;
        private bool _stateCaptured;

        /// <summary>Detaches the prior provider state without destroying pre-existing settings.</summary>
        [SetUp]
        public void PreserveProviderState()
        {
            Assert.That(
                Application.isPlaying,
                Is.True,
                "Player lifetime controls require PlayMode."
            );
            Assert.That(Cache, Is.Not.Null, "The actual provider cache must be available.");
            Assert.That(
                Resources.Load<DxMessagingRuntimeSettings>(DxMessagingRuntimeSettings.ResourceName)
                    == null,
                Is.True,
                "The isolated host must have no settings resource asset for fallback controls."
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
        }

        /// <summary>Drains only test-owned destruction and then restores the prior provider state.</summary>
        [UnityTearDown]
        public IEnumerator RestoreProviderState()
        {
            if (!_stateCaptured)
            {
                yield break;
            }
            Cache.SetValue(null, null);
            Owned?.SetValue(null, null);
            _retainedFallback?.SetValue(_ownership, null);
            try
            {
                foreach (DxMessagingRuntimeSettings settings in _created)
                {
                    if (settings != null)
                    {
                        Object.Destroy(settings);
                    }
                }
                yield return null;
                Assert.That(
                    _created.TrueForAll(settings => settings == null),
                    Is.True,
                    "Every test-owned fallback must finish native destruction during cleanup."
                );
            }
            finally
            {
                Cache.SetValue(null, _savedCache);
                Owned?.SetValue(null, _savedOwned);
                _retainedFallback?.SetValue(_ownership, _savedRetained);
                _created.Clear();
                _stateCaptured = false;
            }
        }

#if UNITY_EDITOR
        /// <summary>Editor reload cleanup releases its native fallback before the next player frame.</summary>
        [Test]
        public void EditorReloadCleanupReleasesFallbackImmediatelyWhilePlaying()
        {
            DxMessagingRuntimeSettings settings = DxMessagingRuntimeSettingsProvider.Current;
            _created.Add(settings);
            Assert.That(
                settings.IsFallbackInstance,
                Is.True,
                "The actual provider must own the fallback."
            );
            MethodInfo cleanup = Provider.GetMethod("ReleaseBeforeAssemblyReload", StaticFields);
            Assert.That(cleanup, Is.Not.Null, "The Editor needs a reload cleanup boundary.");
            cleanup.Invoke(null, null);
            Assert.That(
                settings == null,
                Is.True,
                "Editor reload must release its fallback before a frame can run, even while playing."
            );
            Assert.That(
                Cache.GetValue(null),
                Is.Null,
                "Reload cleanup must detach the cache immediately."
            );
            Assert.That(
                Owned.GetValue(null),
                Is.Null,
                "Reload cleanup must detach static ownership."
            );
            Assert.That(
                _retainedFallback.GetValue(_ownership),
                Is.Null,
                "Reload cleanup must detach serialized ownership."
            );
        }
#endif

        /// <summary>Resets invalidate ownership immediately and destroy each fallback after one frame.</summary>
        [UnityTest]
        public IEnumerator RepeatedPlayerResetsReleaseFallbacksAtTheFrameBoundary()
        {
            for (int cycle = 0; cycle < 3; ++cycle)
            {
                DxMessagingRuntimeSettings settings = DxMessagingRuntimeSettingsProvider.Current;
                _created.Add(settings);
                Assert.That(
                    settings.IsFallbackInstance,
                    Is.True,
                    "The actual provider must create a fallback."
                );
                Assert.That(
                    DxMessagingRuntimeSettingsProvider.Current,
                    Is.SameAs(settings),
                    "Reads before reset must keep the same live fallback."
                );
                DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
                Assert.That(
                    Cache.GetValue(null),
                    Is.Null,
                    "Reset must detach the cache immediately."
                );
                Assert.That(
                    settings != null,
                    Is.True,
                    "Player destruction must be deferred until the frame ends."
                );
                Assert.DoesNotThrow(
                    DxMessagingRuntimeSettingsProvider.ResetCachedSettings,
                    "A second reset must remain safe while destruction is pending."
                );
                yield return null;
                Assert.That(
                    settings == null,
                    Is.True,
                    "The provider-created fallback must be destroyed after the frame boundary."
                );
            }
        }
    }
}
#endif
