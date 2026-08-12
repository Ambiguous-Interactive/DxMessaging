namespace DxMessaging.Core.Configuration
{
    using System;
    using System.Threading;
    using MessageBus;
#if UNITY_2021_3_OR_NEWER
    using UnityEngine;
#endif

#if UNITY_2021_3_OR_NEWER
    /// <summary>
    /// Lazy provider that hands out the active <see cref="DxMessagingRuntimeSettings"/>
    /// instance. Tries <c>Resources.Load&lt;DxMessagingRuntimeSettings&gt;("DxMessagingRuntimeSettings")</c>
    /// once per AppDomain; on miss, returns a defaulted in-memory instance so the
    /// runtime always has a usable settings object.
    /// </summary>
    /// <remarks>
    /// Tests inject a fake via <see cref="Override"/>, which returns an
    /// <see cref="IDisposable"/> that restores the nearest active prior settings,
    /// including when nested tokens are disposed out of order. Each call allocates
    /// one sealed reference token. Override raises
    /// <see cref="DxMessagingRuntimeSettings.SettingsChanged"/> on push and when
    /// disposing the newest active token changes the current settings.
    /// </remarks>
    public static class DxMessagingRuntimeSettingsProvider
    {
        private static DxMessagingRuntimeSettings _cached;
        private static readonly object _gate = new();
        private static OverrideToken _activeOverride;

        /// <summary>
        /// Returns the active settings instance. Loads the asset on first call;
        /// subsequent calls return the cached reference (or a test override if
        /// <see cref="Override"/> is active).
        /// </summary>
        /// <remarks>
        /// In non-Unity builds (where <c>UNITY_2021_3_OR_NEWER</c> is not defined)
        /// this property returns <c>null</c> because <c>ScriptableObject</c> is
        /// unavailable. Callers must tolerate a <c>null</c> result outside Unity.
        /// </remarks>
        public static DxMessagingRuntimeSettings Current
        {
            get
            {
                DxMessagingRuntimeSettings local = Volatile.Read(ref _cached);
                if (local != null)
                {
                    return local;
                }
                lock (_gate)
                {
                    local = _cached;
                    if (local != null)
                    {
                        return local;
                    }
                    local = LoadOrCreate();
                    Volatile.Write(ref _cached, local);
                    return local;
                }
            }
        }

        /// <summary>
        /// Pushes a test-supplied settings instance as the active <see cref="Current"/>
        /// value and raises <see cref="DxMessagingRuntimeSettings.SettingsChanged"/>.
        /// Disposing the returned token restores the previous instance and raises
        /// the event again so subscribers re-apply the original caps. Nested tokens
        /// may be disposed in any order; an ended ancestor is skipped when the newest
        /// token later restores state. Each call allocates one sealed reference token.
        /// </summary>
        public static IDisposable Override(DxMessagingRuntimeSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            OverrideToken token;
            lock (_gate)
            {
                token = new OverrideToken(
                    settings,
                    _cached,
                    IMessageBus.GlobalMessageBufferSize,
                    _activeOverride
                );
                if (_activeOverride != null)
                {
                    _activeOverride._newer = token;
                }
                _activeOverride = token;
                _cached = settings;
            }
            DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
            return token;
        }

        /// <summary>
        /// Clears the cached reference, forcing the next <see cref="Current"/>
        /// access to reload from <c>Resources</c>. Test-only.
        /// </summary>
        internal static void ResetForTests()
        {
            lock (_gate)
            {
                OverrideToken token = _activeOverride;
                while (token != null)
                {
                    token = token.InvalidateAndGetOlder();
                }
                _activeOverride = null;
                _cached = null;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnSubsystemRegistration()
        {
            ResetForTests();
        }

        private static DxMessagingRuntimeSettings LoadOrCreate()
        {
            DxMessagingRuntimeSettings asset = null;
            try
            {
                asset = Resources.Load<DxMessagingRuntimeSettings>(
                    DxMessagingRuntimeSettings.ResourceName
                );
            }
            catch
            {
                asset = null;
            }
            if (asset != null)
            {
                return asset;
            }
            DxMessagingRuntimeSettings fallback =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            fallback.MarkAsFallbackInstance();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.name = DxMessagingRuntimeSettings.ResourceName + " (Default)";
            return fallback;
        }

        private sealed class OverrideToken : IDisposable
        {
            private DxMessagingRuntimeSettings _installed;
            private int _previousGlobalMessageBufferSize;
            private DxMessagingRuntimeSettings _previous;
            private OverrideToken _older;
            internal OverrideToken _newer;
            private bool _disposed;

            public OverrideToken(
                DxMessagingRuntimeSettings installed,
                DxMessagingRuntimeSettings previous,
                int previousGlobalMessageBufferSize,
                OverrideToken older
            )
            {
                _installed = installed;
                _previous = previous;
                _previousGlobalMessageBufferSize = previousGlobalMessageBufferSize;
                _older = older;
            }

            public void Dispose()
            {
                DxMessagingRuntimeSettings restored = null;
                bool didRestore = false;
                int restoredGlobalMessageBufferSize = 0;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;

                    if (_newer != null)
                    {
                        // The immediate child used this override as its restore point.
                        // Bypass the ended node and carry the root snapshot forward so
                        // the newest live override eventually restores the nearest
                        // active ancestor regardless of disposal order.
                        _newer._older = _older;
                        _newer._previous = _previous;
                        _newer._previousGlobalMessageBufferSize = _previousGlobalMessageBufferSize;
                        if (_older != null)
                        {
                            _older._newer = _newer;
                        }
                    }
                    else
                    {
                        _activeOverride = _older;
                        if (_older != null)
                        {
                            _older._newer = null;
                        }
                        restored = _previous;
                        restoredGlobalMessageBufferSize = _previousGlobalMessageBufferSize;
                        _cached = restored;
                        didRestore = true;
                    }

                    _installed = null;
                    _previous = null;
                    _older = null;
                    _newer = null;
                }
                if (didRestore)
                {
                    if (restored == null || restored.IsFallbackInstance)
                    {
                        IMessageBus.GlobalMessageBufferSize = restoredGlobalMessageBufferSize;
                    }
                    DxMessagingRuntimeSettings.RaiseSettingsChanged(restored);
                }
            }

            internal OverrideToken InvalidateAndGetOlder()
            {
                OverrideToken older = _older;
                _disposed = true;
                _installed = null;
                _previous = null;
                _older = null;
                _newer = null;
                return older;
            }
        }
    }
#endif
}
