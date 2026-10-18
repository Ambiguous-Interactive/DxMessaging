#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using DxMessaging.Editor.Settings;
    using UnityEditor;
    using UnityEngine;

    internal sealed class EditorSettingsCacheScope : IDisposable
    {
        private readonly EditorStaticFieldScope _state = new(
            typeof(DxMessagingSettings),
            "_settingsRevision",
            "_cachedSettingsRevision",
            "_cachedSettings",
            "_cachedSettingsPath"
        );

        internal void SetCurrent(DxMessagingSettings settings, string path = null)
        {
            _state.Set("_cachedSettings", settings);
            _state.Set(
                "_cachedSettingsPath",
                path ?? (settings == null ? null : AssetDatabase.GetAssetPath(settings))
            );
            _state.Set("_cachedSettingsRevision", DxMessagingSettings.SettingsRevision);
        }

        public void Dispose() => _state.Dispose();
    }

    /// <summary>Preserves the real generated inputs touched by editor settings operations.</summary>
    internal sealed class EditorCompilerInputScope : IDisposable
    {
        private readonly Dictionary<string, string> _saved = new();
        private readonly Dictionary<string, object> _pending = new();
        private bool _assetEditing;

        internal EditorCompilerInputScope()
        {
            _pending.Add(
                "sidecar",
                MessageBusInspection.ReadField(
                    typeof(DxMessagingBaseCallIgnoreSync),
                    "SidecarImportPending"
                )
            );
            _pending.Add(
                "response",
                MessageBusInspection.ReadField(
                    typeof(DxMessaging.Editor.SetupCscRsp),
                    "responseFileImportPending"
                )
            );
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (
                string relative in new[]
                {
                    "Assets/csc.rsp",
                    "csc.rsp",
                    DxMessagingBaseCallIgnoreSync.SidecarAssetPath,
                }
            )
            {
                foreach (string suffix in new[] { "", ".meta" })
                {
                    string path = Path.Combine(root, relative + suffix);
                    string backup = File.Exists(path)
                        ? Path.Combine(
                            Path.GetDirectoryName(path),
                            "."
                                + Path.GetFileName(path)
                                + ".dxm-fixture-"
                                + Guid.NewGuid().ToString("N")
                        )
                        : null;
                    _saved.Add(path, backup);
                }
            }
            try
            {
                /*
                    Importing temporary compiler inputs requests a compilation while NUnit locks
                    reloads. Keep imports batched until their original bytes are restored.
                */
                AssetDatabase.StartAssetEditing();
                _assetEditing = true;
                foreach (KeyValuePair<string, string> saved in _saved)
                {
                    if (saved.Value != null)
                    {
                        File.Move(saved.Key, saved.Value);
                        File.Copy(saved.Value, saved.Key);
                    }
                }
            }
            catch (Exception failure)
            {
                try
                {
                    Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(failure, cleanupFailure);
                }
                throw;
            }
        }

        public void Dispose()
        {
            List<Exception> failures = new();
            foreach (KeyValuePair<string, string> saved in _saved)
            {
                try
                {
                    if (saved.Value == null)
                    {
                        if (File.Exists(saved.Key))
                        {
                            File.Delete(saved.Key);
                        }
                    }
                    else if (File.Exists(saved.Value))
                    {
                        if (File.Exists(saved.Key))
                        {
                            File.Delete(saved.Key);
                        }
                        File.Move(saved.Value, saved.Key);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            try
            {
                typeof(DxMessagingBaseCallIgnoreSync)
                    .GetField(
                        "SidecarImportPending",
                        System.Reflection.BindingFlags.Static
                            | System.Reflection.BindingFlags.NonPublic
                    )
                    .SetValue(null, _pending["sidecar"]);
                typeof(DxMessaging.Editor.SetupCscRsp)
                    .GetField(
                        "responseFileImportPending",
                        System.Reflection.BindingFlags.Static
                            | System.Reflection.BindingFlags.NonPublic
                    )
                    .SetValue(null, _pending["response"]);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                if (_assetEditing)
                {
                    _assetEditing = false;
                    try
                    {
                        AssetDatabase.StopAssetEditing();
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }
            }
            if (0 < failures.Count)
            {
                throw new AggregateException("Failed to restore owned compiler inputs.", failures);
            }
        }
    }
}
#endif
