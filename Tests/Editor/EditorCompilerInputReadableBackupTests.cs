#if UNITY_EDITOR_WIN
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class EditorCompilerInputReadableBackupTests
    {
        /// <remarks>
        /// 2026-10-31: backing up compiler inputs must not rename or recreate a readable
        /// original. A read-sharing lease denies deletion but permits an independent copy.
        /// This owned reader does not identify the holder in historical sharing failures.
        /// </remarks>
        [TestCase(false, TestName = "CompilerInputReadableBackup_readerFalse")]
        [TestCase(true, TestName = "CompilerInputReadableBackup_readerTrue")]
        public void ReadableOriginalSurvivesBackup(bool readerHeld)
        {
            AssertIndependentCopyOracle();
            string path = Path.Combine(Application.dataPath, "csc.rsp");
            Assert.That(File.Exists(path), Is.True, "Use the actual prepared compiler input.");
            byte[] baseline = File.ReadAllBytes(path);
            List<string> failures = new();
            for (int cycle = 0; cycle < 10; ++cycle)
            {
                CollectionAssert.AreEqual(baseline, File.ReadAllBytes(path));
                DateTime modified = File.GetLastWriteTimeUtc(path);
                FileStream reader = null;
                EditorCompilerInputScope scope = null;
                Exception constructorError = null;
                Exception cleanupError = null;
                bool sourceReadable = false;
                bool sourceExact = false;
                bool scopeCreated = false;
                int leaseDeleteError = -1;
                try
                {
                    if (readerHeld)
                    {
                        reader = new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read
                        );
                        Assert.That(reader.CanRead, Is.True);
                        Assert.That(reader.Length, Is.EqualTo(baseline.LongLength));
                    }
                    leaseDeleteError = EditorCompilerInputFailureDiagnostics.Probe(path).Error;
                    Assert.That(leaseDeleteError, Is.EqualTo(readerHeld ? 32 : 0));
                    CollectionAssert.AreEqual(baseline, File.ReadAllBytes(path));
                    try
                    {
                        scope = new EditorCompilerInputScope();
                        scopeCreated = true;
                    }
                    catch (Exception error)
                    {
                        constructorError = error;
                    }
                    sourceReadable = File.Exists(path);
                    sourceExact = sourceReadable && baseline.SequenceEqual(File.ReadAllBytes(path));
                }
                finally
                {
                    reader?.Dispose();
                    try
                    {
                        scope?.Dispose();
                    }
                    catch (Exception error)
                    {
                        cleanupError = error;
                    }
                }
                bool restored =
                    File.Exists(path) && baseline.SequenceEqual(File.ReadAllBytes(path));
                bool modifiedUnchanged = File.GetLastWriteTimeUtc(path) == modified;
                int nativeError =
                    constructorError == null ? 0 : Innermost(constructorError).HResult & 65535;
                bool passed =
                    constructorError == null
                    && cleanupError == null
                    && scopeCreated
                    && sourceReadable
                    && sourceExact
                    && restored
                    && modifiedUnchanged;
                string observation =
                    $"reader={readerHeld}; cycle={cycle}; leaseDeleteError={leaseDeleteError}; oracleCopy=True; sourceReadable={sourceReadable}; sourceExact={sourceExact}; restored={restored}; modifiedUnchanged={modifiedUnchanged}; scopeCreated={scopeCreated}; constructorError={constructorError?.GetType().FullName ?? "none"}; nativeError={nativeError}; cleanupError={cleanupError?.GetType().FullName ?? "none"}; passed={passed}";
                TestContext.Out.WriteLine("DXM_COMPILER_INPUT_READABLE_BACKUP " + observation);
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

        private static Exception Innermost(Exception error)
        {
            while (error.InnerException != null)
            {
                error = error.InnerException;
            }
            return error;
        }

        private static void AssertIndependentCopyOracle()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "DxmReadableBackup" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(root);
            try
            {
                string original = Path.Combine(root, "input.rsp");
                string backup = Path.Combine(root, ".input.rsp.backup");
                byte[] expected = { 7, 11, 13, 17 };
                File.WriteAllBytes(original, expected);
                using (
                    FileStream reader = new(
                        original,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read
                    )
                )
                {
                    Assert.That(reader.CanRead, Is.True);
                    Assert.That(
                        EditorCompilerInputFailureDiagnostics.Probe(original).Error,
                        Is.EqualTo(32)
                    );
                    File.Copy(original, backup);
                    CollectionAssert.AreEqual(expected, File.ReadAllBytes(original));
                    CollectionAssert.AreEqual(expected, File.ReadAllBytes(backup));
                }
                TestContext.Out.WriteLine(
                    "DXM_COMPILER_INPUT_READABLE_BACKUP_ORACLE copy=True; readerDeleteError=32; originalExact=True; backupExact=True"
                );
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
#endif
