#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Editor.Settings;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;

    public sealed class EditorCompilerInputPartialBackupTests
    {
        private static readonly string[] InputPaths =
        {
            "Assets/csc.rsp",
            "Assets/csc.rsp.meta",
            "csc.rsp",
            "csc.rsp.meta",
            DxMessagingBaseCallIgnoreSync.SidecarAssetPath,
            DxMessagingBaseCallIgnoreSync.SidecarAssetPath + ".meta",
        };

        /// <remarks>
        /// 2026-11-13: disposal deleted unchanged compiler inputs. That needlessly required
        /// delete sharing on Windows and replaced the file behind open readers on Unix.
        /// The owned reader establishes this conditional defect, not a historical lock owner.
        /// </remarks>
        [TestCaseSource(nameof(UnchangedInputCases))]
        public void UnchangedCompilerInputKeepsOpenReader(string relativePath)
        {
            using EditorCompilerInputScope originals = new();
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.Combine(root, relativePath);
            byte[] expected = { 7, 11, 13, 17 };
            byte[] changed = { 19, 23, 29, 31 };
            for (int cycle = 0; cycle < 10; ++cycle)
            {
                string context = $"path={relativePath}, cycle={cycle}";
                File.WriteAllBytes(path, expected);
                DateTime modified = File.GetLastWriteTimeUtc(path);
                string[] backups = BackupFiles(new[] { path });
                EditorCompilerInputScope scope = null;
                FileStream reader = null;
                try
                {
                    reader = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite
                    );
                    scope = new();
                    scope.Dispose();
                    CollectionAssert.AreEqual(expected, File.ReadAllBytes(path), context);
                    Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(modified), context);
                    CollectionAssert.AreEqual(backups, BackupFiles(new[] { path }), context);
                    scope.Dispose();
                    File.WriteAllBytes(path, changed);
                    byte[] observed = new byte[changed.Length];
                    Assert.That(
                        reader.Read(observed, 0, observed.Length),
                        Is.EqualTo(observed.Length),
                        context
                    );
                    CollectionAssert.AreEqual(
                        changed,
                        observed,
                        $"{context} An unchanged input must retain the file behind its open reader."
                    );
                }
                finally
                {
                    reader?.Dispose();
                    scope?.Dispose();
                }
            }
        }

        private static IEnumerable<TestCaseData> UnchangedInputCases() =>
            InputPaths.Select(path =>
                new TestCaseData(path).SetName($"CompilerInputUnchanged_{path}")
            );

        [TestCaseSource(nameof(RestoredInputCases))]
        public void ChangedCompilerInputRestoresOriginal(string relativePath, string mutation)
        {
            using EditorCompilerInputScope originals = new();
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.Combine(root, relativePath);
            byte[] expected = { 7, 11, 13, 17 };
            string context = $"path={relativePath}, mutation={mutation}";
            if (mutation == "created")
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, expected);
            }
            DateTime modified = File.GetLastWriteTimeUtc(path);
            string[] backups = BackupFiles(new[] { path });
            using (EditorCompilerInputScope scope = new())
            {
                if (mutation == "removed")
                {
                    File.Delete(path);
                }
                else if (mutation == "retouched")
                {
                    File.SetLastWriteTimeUtc(path, modified.AddMinutes(-1));
                }
                else
                {
                    File.WriteAllBytes(
                        path,
                        mutation == "same-length"
                            ? new byte[] { 19, 23, 29, 31 }
                            : new byte[] { 19, 23, 29 }
                    );
                }
                scope.Dispose();
                Assert.That(File.Exists(path), Is.EqualTo(mutation != "created"), context);
                if (mutation != "created")
                {
                    CollectionAssert.AreEqual(expected, File.ReadAllBytes(path), context);
                    Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(modified), context);
                }
                CollectionAssert.AreEqual(backups, BackupFiles(new[] { path }), context);
            }
        }

        private static IEnumerable<TestCaseData> RestoredInputCases()
        {
            foreach (string path in InputPaths)
            {
                foreach (
                    string mutation in new[]
                    {
                        "modified",
                        "same-length",
                        "removed",
                        "created",
                        "retouched",
                    }
                )
                {
                    yield return new TestCaseData(path, mutation).SetName(
                        $"CompilerInputRestored_{path}_{mutation}"
                    );
                }
            }
        }

        /// <remarks>
        /// 2026-11-01: a failed backup can leave partial bytes. Constructor cleanup must
        /// preserve originals instead of restoring an incomplete backup. The injected
        /// copy failure tests this conditional contract, not a native Windows copy failure.
        /// The default constructor still uses File.Copy. An outer scope owns real inputs.
        /// </remarks>
        [TestCase("Assets/csc.rsp", false, TestName = "CompilerInputPartialBackup_assetsFalse")]
        [TestCase("Assets/csc.rsp", true, TestName = "CompilerInputPartialBackup_assetsTrue")]
        [TestCase("csc.rsp", false, TestName = "CompilerInputPartialBackup_rootFalse")]
        [TestCase("csc.rsp", true, TestName = "CompilerInputPartialBackup_rootTrue")]
        public void FailedConstructionPreservesOriginals(
            string failedRelativePath,
            bool injectFailure
        )
        {
            Type mono = typeof(object).Assembly.GetType("Mono.Runtime");
            Assert.That(mono, Is.Not.Null, "This is an Editor Mono observation.");
            MethodInfo displayName = mono.GetMethod(
                "GetDisplayName",
                BindingFlags.Static | BindingFlags.NonPublic
            );
            Assert.That(displayName, Is.Not.Null);
            TestContext.Out.WriteLine(
                $"DXM_PARTIAL_BACKUP_RUNTIME unity={Application.unityVersion}; mono={displayName.Invoke(null, null)}; clr={Environment.Version}; platform={Application.platform}; coreLibrary={typeof(object).Assembly.FullName}; coreModule={typeof(object).Module.ModuleVersionId}; coreLocation={typeof(object).Assembly.Location}"
            );

            using EditorCompilerInputScope originals = new();
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string target = Path.Combine(root, failedRelativePath);
            string[] paths = new[]
            {
                "Assets/csc.rsp",
                "csc.rsp",
                DxMessagingBaseCallIgnoreSync.SidecarAssetPath,
            }
                .SelectMany(path =>
                    new[] { Path.Combine(root, path), Path.Combine(root, path + ".meta") }
                )
                .ToArray();
            byte[] expected = new byte[8192];
            for (int index = 0; index < expected.Length; ++index)
            {
                expected[index] = (byte)((index * 17 + 31) % 251);
            }
            File.WriteAllBytes(paths[0], expected);
            File.WriteAllBytes(paths[2], expected);
            Dictionary<string, byte[]> baseline = paths.ToDictionary(
                path => path,
                path => File.Exists(path) ? File.ReadAllBytes(path) : null
            );
            List<string> failures = new();
            for (int cycle = 0; cycle < 10; ++cycle)
            {
                foreach (KeyValuePair<string, byte[]> saved in baseline)
                {
                    if (saved.Value != null)
                    {
                        File.WriteAllBytes(saved.Key, saved.Value);
                    }
                }
                Delegate[] callbacks = EditorApplication.delayCall?.GetInvocationList()
                    ?? Array.Empty<Delegate>();
                object sidecarPending = MessageBusInspection.ReadField(
                    typeof(DxMessagingBaseCallIgnoreSync),
                    "SidecarImportPending"
                );
                object responsePending = MessageBusInspection.ReadField(
                    typeof(DxMessaging.Editor.SetupCscRsp),
                    "responseFileImportPending"
                );
                string[] backups = BackupFiles(paths);
                IOException injected = new("Owned partial compiler-input backup failure.");
                Exception constructorError = null;
                Exception cleanupError = null;
                bool partialWritten = false;
                bool scopeCreated = false;
                int completedCopies = 0;
                EditorCompilerInputScope scope = null;
                try
                {
                    scope = new EditorCompilerInputScope(
                        (source, destination) =>
                        {
                            if (injectFailure && source == target)
                            {
                                using (
                                    FileStream partial = new(
                                        destination,
                                        FileMode.CreateNew,
                                        FileAccess.Write
                                    )
                                )
                                {
                                    partial.Write(expected, 0, 4096);
                                }
                                partialWritten = File.ReadAllBytes(destination)
                                    .SequenceEqual(expected.Take(4096));
                                throw injected;
                            }
                            File.Copy(source, destination);
                            ++completedCopies;
                        }
                    );
                    scopeCreated = true;
                    File.WriteAllText(paths[0], "temporary first input");
                    File.WriteAllText(paths[2], "temporary later input");
                }
                catch (Exception error)
                {
                    constructorError = error;
                }
                finally
                {
                    try
                    {
                        scope?.Dispose();
                    }
                    catch (Exception error)
                    {
                        cleanupError = error;
                    }
                }
                bool originalsExact = baseline.All(saved =>
                    saved.Value == null
                        ? !File.Exists(saved.Key)
                        : File.Exists(saved.Key)
                            && saved.Value.SequenceEqual(File.ReadAllBytes(saved.Key))
                );
                bool callbacksExact = callbacks.SequenceEqual(
                    EditorApplication.delayCall?.GetInvocationList() ?? Array.Empty<Delegate>()
                );
                bool pendingExact =
                    Equals(
                        sidecarPending,
                        MessageBusInspection.ReadField(
                            typeof(DxMessagingBaseCallIgnoreSync),
                            "SidecarImportPending"
                        )
                    )
                    && Equals(
                        responsePending,
                        MessageBusInspection.ReadField(
                            typeof(DxMessaging.Editor.SetupCscRsp),
                            "responseFileImportPending"
                        )
                    );
                bool backupsExact = backups.SequenceEqual(BackupFiles(paths));
                bool exceptionExact = injectFailure
                    ? constructorError is IOException
                        && ReferenceEquals(constructorError.InnerException, injected)
                    : constructorError == null;
                bool orderExact = failedRelativePath == "Assets/csc.rsp" || 0 < completedCopies;
                bool passed =
                    originalsExact
                    && callbacksExact
                    && pendingExact
                    && backupsExact
                    && exceptionExact
                    && orderExact
                    && cleanupError == null
                    && scopeCreated == !injectFailure
                    && partialWritten == injectFailure;
                string observation =
                    $"target={failedRelativePath}; injected={injectFailure}; cycle={cycle}; partialWritten={partialWritten}; completedCopies={completedCopies}; originalsExact={originalsExact}; targetLength={new FileInfo(target).Length}; callbacksExact={callbacksExact}; pendingExact={pendingExact}; backupsExact={backupsExact}; exceptionExact={exceptionExact}; scopeCreated={scopeCreated}; cleanupError={cleanupError?.GetType().FullName ?? "none"}; passed={passed}";
                TestContext.Out.WriteLine("DXM_PARTIAL_BACKUP " + observation);
                if (!passed)
                {
                    failures.Add(observation);
                    if (constructorError != null)
                    {
                        failures.Add("Original constructor exception: " + constructorError);
                    }
                    if (cleanupError != null)
                    {
                        failures.Add("Original cleanup exception: " + cleanupError);
                    }
                }
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        private static string[] BackupFiles(string[] paths) =>
            paths
                .SelectMany(path =>
                    Directory.Exists(Path.GetDirectoryName(path))
                        ? Directory.GetFiles(
                            Path.GetDirectoryName(path),
                            "." + Path.GetFileName(path) + ".dxm-fixture-*"
                        )
                        : Array.Empty<string>()
                )
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
    }
}
#endif
