#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests
{
    using System;
    using System.Reflection;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Core.MessageBus;

    /// <summary>Owns fixture settings through the real provider cache and production change notification.</summary>
    internal static class RuntimeSettingsFixture
    {
        private static readonly FieldInfo CachedField =
            typeof(DxMessagingRuntimeSettingsProvider).GetField(
                "_cached",
                BindingFlags.Static | BindingFlags.NonPublic
            );
        private static readonly object _gate = MessageBusInspection.ReadField(
            typeof(DxMessagingRuntimeSettingsProvider),
            "_gate"
        );
        private static OverrideToken _activeOverride;
        private static DxMessagingRuntimeSettings _cached
        {
            get => (DxMessagingRuntimeSettings)CachedField.GetValue(null);
            set => CachedField.SetValue(null, value);
        }

        private static void InvalidateScopes()
        {
            OverrideToken current = _activeOverride;
            while (current != null)
            {
                current = current.InvalidateAndGetOlder();
            }
            _activeOverride = null;
        }

        private static void InvalidateDetachedScopes()
        {
            if (_activeOverride != null && !ReferenceEquals(_cached, _activeOverride._installed))
            {
                InvalidateScopes();
            }
        }

        internal static void Reset()
        {
            lock (_gate)
            {
                InvalidateScopes();
                DxMessagingRuntimeSettingsProvider.ResetCachedSettings();
            }
        }

        /// <summary>
        /// Pushes a test-supplied settings instance as the active <see cref="DxMessagingRuntimeSettingsProvider.Current"/>
        /// value and raises <see cref="DxMessagingRuntimeSettings.SettingsChanged"/>.
        /// Disposing the returned token restores the previous instance and raises
        /// the event again so subscribers re-apply the original caps. Nested tokens
        /// may be disposed in any order; an ended ancestor is skipped when the newest
        /// token later restores state. Each call allocates one sealed reference token.
        /// </summary>
        internal static IDisposable Override(DxMessagingRuntimeSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            OverrideToken token;
            lock (_gate)
            {
                InvalidateDetachedScopes();
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

        private sealed class OverrideToken : IDisposable
        {
            internal DxMessagingRuntimeSettings _installed;
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
                    InvalidateDetachedScopes();
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;

                    if (_newer != null)
                    {
                        /*
                            The immediate child used this override as its restore point.
                            Bypass the ended node and carry the root snapshot forward so
                            the newest live override eventually restores the nearest
                            active ancestor regardless of disposal order.
                        */
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
}
#endif
