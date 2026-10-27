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

    internal readonly struct DeleteSharingProbeResult
    {
        internal readonly int Error;
        internal readonly long Started;
        internal readonly long Finished;

        internal DeleteSharingProbeResult(int error, long started, long finished)
        {
            Error = error;
            Started = started;
            Finished = finished;
        }
    }

    /// <summary>Observes DELETE sharing without modifying the file or retrying its failed operation.</summary>
    internal static class EditorCompilerInputFailureDiagnostics
    {
        internal static DeleteSharingProbeResult Probe(string path)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int error = -1;
#if UNITY_EDITOR_WIN
            try
            {
                IntPtr handle = CreateFileW(
                    Path.GetFullPath(path),
                    0x00010000,
                    7,
                    IntPtr.Zero,
                    3,
                    0,
                    IntPtr.Zero
                );
                if (handle == new IntPtr(-1))
                {
                    error = Marshal.GetLastWin32Error();
                }
                else
                {
                    error = CloseHandle(handle) ? 0 : -2;
                }
            }
            catch (Exception)
            {
                error = -1;
            }
#endif
            return new DeleteSharingProbeResult(
                error,
                started,
                System.Diagnostics.Stopwatch.GetTimestamp()
            );
        }

        internal static string Describe(string path, Exception failure, string operation)
        {
            long observed = System.Diagnostics.Stopwatch.GetTimestamp();
            DeleteSharingProbeResult before = Probe(path);
            long queryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            string owners = EditorFileLockDiagnostics.Describe(path);
            long queryFinished = System.Diagnostics.Stopwatch.GetTimestamp();
            DeleteSharingProbeResult after = Probe(path);
            return $"DXM_COMPILER_INPUT_SHARING operation={operation}; code={failure.HResult & 65535}; "
                + $"before={before.Error}; after={after.Error}; observed={observed}; "
                + $"beforeStart={before.Started}; beforeEnd={before.Finished}; "
                + $"queryStart={queryStarted}; queryEnd={queryFinished}; "
                + $"afterStart={after.Started}; afterEnd={after.Finished}; "
                + $"frequency={System.Diagnostics.Stopwatch.Frequency}; rm={owners}";
        }

#if UNITY_EDITOR_WIN
        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true
        )]
        private static extern IntPtr CreateFileW(
            string fileName,
            uint desiredAccess,
            uint sharing,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flags,
            IntPtr template
        );

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
#endif
    }

#if UNITY_EDITOR_WIN
    [TestFixture]
    public sealed class EditorDeleteSharingProbeTests
    {
        [TestCase("held-denial", 32)]
        [TestCase("released-denial", 0)]
        [TestCase("delete-shared", 0)]
        [TestCase("unlocked", 0)]
        [TestCase("missing", 2)]
        public void DeleteSharingProbeReportsOwnedFileState(string state, int expectedError)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "dxm-delete-sharing-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "owned.rsp");
            FileStream owner = null;
            try
            {
                if (state != "missing")
                {
                    File.WriteAllText(path, "owned control");
                }
                if (
                    state == "held-denial"
                    || state == "released-denial"
                    || state == "delete-shared"
                )
                {
                    FileShare sharing = FileShare.ReadWrite;
                    if (state == "delete-shared")
                    {
                        sharing |= FileShare.Delete;
                    }
                    owner = new FileStream(path, FileMode.Open, FileAccess.Read, sharing);
                }
                if (state == "held-denial" || state == "released-denial")
                {
                    IOException failure = Assert.Throws<IOException>(() => File.Delete(path));
                    Assert.That(failure.HResult & 65535, Is.EqualTo(32), state);
                }
                if (state == "released-denial")
                {
                    owner.Dispose();
                    owner = null;
                }
                DeleteSharingProbeResult probe = EditorCompilerInputFailureDiagnostics.Probe(path);
                TestContext.Out.WriteLine(
                    $"DXM_DELETE_SHARING_CONTROL state={state}; error={probe.Error}; "
                        + $"started={probe.Started}; finished={probe.Finished}; "
                        + $"frequency={System.Diagnostics.Stopwatch.Frequency}"
                );
                Assert.That(probe.Error, Is.EqualTo(expectedError), state);
                Assert.That(probe.Started, Is.LessThanOrEqualTo(probe.Finished), state);
                Assert.That(File.Exists(path), Is.EqualTo(state != "missing"), state);
                if (state != "missing")
                {
                    Assert.That(File.ReadAllText(path), Is.EqualTo("owned control"), state);
                }
                GC.KeepAlive(owner);
            }
            finally
            {
                owner?.Dispose();
                Directory.Delete(directory, true);
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
                            string diagnostics = EditorCompilerInputFailureDiagnostics.Describe(
                                saved.Key,
                                failure,
                                operation
                            );
                            throw new IOException(
                                $"Compiler-input backup {operation} failed: '{saved.Key}' -> '{saved.Value}'. "
                                    + $"sourceExists={File.Exists(saved.Key)}, backupExists={File.Exists(saved.Value)}, "
                                    + $"assetEditing={_assetEditing}, compiling={EditorApplication.isCompiling}, "
                                    + $"updating={EditorApplication.isUpdating}. "
                                    + diagnostics,
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
                string operation = "inspect";
                try
                {
                    if (saved.Value == null)
                    {
                        if (File.Exists(saved.Key))
                        {
                            operation = "delete";
                            File.Delete(saved.Key);
                        }
                    }
                    else if (File.Exists(saved.Value))
                    {
                        if (File.Exists(saved.Key))
                        {
                            operation = "delete";
                            File.Delete(saved.Key);
                        }
                        operation = "move";
                        File.Move(saved.Value, saved.Key);
                    }
                }
                catch (Exception exception)
                {
                    string diagnostics = EditorCompilerInputFailureDiagnostics.Describe(
                        saved.Key,
                        exception,
                        operation
                    );
                    failures.Add(
                        new IOException(
                            $"Restoring compiler input '{saved.Key}' failed. "
                                + $"OriginallyPresent={saved.Value != null}, assetEditing={_assetEditing}, "
                                + $"compiling={EditorApplication.isCompiling}, updating={EditorApplication.isUpdating}. "
                                + diagnostics,
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
