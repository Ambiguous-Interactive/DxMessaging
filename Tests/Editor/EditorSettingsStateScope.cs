#if UNITY_EDITOR
// cspell:ignore rstrtmgr
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using DxMessaging.Editor.Settings;
    using UnityEditor;
    using UnityEngine;
#if UNITY_EDITOR_WIN
    using System.Runtime.InteropServices;
    using System.Text;
    using NUnit.Framework;
#endif

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

    /// <summary>Queries locking processes after compiler-input IO fails, without changing them.</summary>
    internal static class EditorFileLockDiagnostics
    {
        internal static string Describe(string path)
        {
#if UNITY_EDITOR_WIN
            if (Marshal.SizeOf<UniqueProcess>() != 12 || Marshal.SizeOf<ProcessInfo>() != 672)
            {
                return "RestartManager observer invalid: unexpected native structure layout.";
            }
            StringBuilder report = new();
            try
            {
                int start = RmStartSession(out uint session, 0, new StringBuilder(33));
                report.Append($"RestartManager start={start}");
                if (start != 0)
                {
                    return report.ToString();
                }
                try
                {
                    int registered = RmRegisterResources(
                        session,
                        1,
                        new[] { path },
                        0,
                        IntPtr.Zero,
                        0,
                        IntPtr.Zero
                    );
                    report.Append($", register={registered}");
                    if (registered == 0)
                    {
                        ProcessInfo[] processes = new ProcessInfo[64];
                        uint count = (uint)processes.Length;
                        int listed = RmGetList(
                            session,
                            out uint needed,
                            ref count,
                            processes,
                            out uint reasons
                        );
                        report.Append(
                            $", list={listed}, needed={needed}, returned={count}, reasons={reasons}"
                        );
                        if (listed == 0)
                        {
                            for (
                                int index = 0;
                                index < Math.Min(count, (uint)processes.Length);
                                ++index
                            )
                            {
                                ProcessInfo process = processes[index];
                                report.Append(
                                    $"; pid={process.Process.Id}, app='{process.ApplicationName}', service='{process.ServiceName}'"
                                );
                            }
                        }
                    }
                }
                finally
                {
                    report.Append($", end={RmEndSession(session)}");
                }
            }
            catch (Exception exception)
            {
                report.Append($"; observer failed: {exception}");
            }
            return report.ToString();
#else
            return "RestartManager unavailable on this Editor platform.";
#endif
        }

#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess
        {
            public uint Id;
            public System.Runtime.InteropServices.ComTypes.FILETIME Started;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessInfo
        {
            public UniqueProcess Process;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
            public string ApplicationName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
            public string ServiceName;
            public uint ApplicationType;
            public uint Status;
            public uint SessionId;

            [MarshalAs(UnmanagedType.Bool)]
            public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RmRegisterResources(
            uint session,
            uint fileCount,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files,
            uint applicationCount,
            IntPtr applications,
            uint serviceCount,
            IntPtr services
        );

        [DllImport("rstrtmgr.dll", ExactSpelling = true)]
        private static extern int RmGetList(
            uint session,
            out uint needed,
            ref uint count,
            [In, Out] ProcessInfo[] processes,
            out uint reasons
        );

        [DllImport("rstrtmgr.dll", ExactSpelling = true)]
        private static extern int RmEndSession(uint session);
#endif
    }

#if UNITY_EDITOR_WIN
    [TestFixture]
    public sealed class EditorFileLockDiagnosticsTests
    {
        [Test]
        public void FileLockObserverReportsTheCurrentProcessForAnOwnedExclusiveFile()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "dxm-owned-lock-" + Guid.NewGuid().ToString("N") + ".rsp"
            );
            try
            {
                using FileStream locked = new(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
                using System.Diagnostics.Process current =
                    System.Diagnostics.Process.GetCurrentProcess();
                string report = EditorFileLockDiagnostics.Describe(path);
                TestContext.Out.WriteLine(report);
                Assert.That(report, Does.StartWith("RestartManager start=0, register=0, list=0,"));
                Assert.That(report, Does.Contain("pid=" + current.Id + ","));
                Assert.That(report, Does.EndWith(", end=0"));
                GC.KeepAlive(locked);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
#endif

    /// <summary>Preserves the real generated inputs touched by editor settings operations.</summary>
    internal sealed class EditorCompilerInputScope : IDisposable
    {
        private readonly Dictionary<string, string> _saved = new();
        private readonly Dictionary<string, object> _pending = new();
        private readonly EditorDelayCallScope _sidecarCallbacks;
        private readonly EditorDelayCallScope _responseCallbacks;
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
            try
            {
                _sidecarCallbacks = new(
                    typeof(DxMessagingBaseCallIgnoreSync),
                    suspendBaseline: true
                );
                _responseCallbacks = new(
                    typeof(DxMessaging.Editor.SetupCscRsp),
                    suspendBaseline: true
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
                        string operation = "move";
                        try
                        {
                            File.Move(saved.Key, saved.Value);
                            operation = "copy";
                            File.Copy(saved.Value, saved.Key);
                        }
                        catch (IOException failure)
                        {
                            throw new IOException(
                                $"Compiler-input backup {operation} failed: '{saved.Key}' -> '{saved.Value}'. "
                                    + $"sourceExists={File.Exists(saved.Key)}, backupExists={File.Exists(saved.Value)}, "
                                    + $"assetEditing={_assetEditing}, compiling={EditorApplication.isCompiling}, "
                                    + $"updating={EditorApplication.isUpdating}. "
                                    + EditorFileLockDiagnostics.Describe(saved.Key),
                                failure
                            );
                        }
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
                    failures.Add(
                        new IOException(
                            $"Restoring compiler input '{saved.Key}' failed. "
                                + $"OriginallyPresent={saved.Value != null}, assetEditing={_assetEditing}, "
                                + $"compiling={EditorApplication.isCompiling}, updating={EditorApplication.isUpdating}.",
                            exception
                        )
                    );
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
            try
            {
                _responseCallbacks?.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            try
            {
                _sidecarCallbacks?.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            if (0 < failures.Count)
            {
                throw new AggregateException("Failed to restore owned compiler inputs.", failures);
            }
        }
    }
}
#endif
