#if UNITY_EDITOR

namespace DxMessaging.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading;
    using DxMessaging.Editor.Settings;
    using UnityEditor;
    using UnityEngine;

    [InitializeOnLoad]
    public static class SetupCscRsp
    {
        internal const string RspAssetPath = "Assets/csc.rsp";
        private static bool responseFileImportPending;
        private static readonly int EditorThreadId = Thread.CurrentThread.ManagedThreadId;

        // Older package versions copied the analyzer + Roslyn runtime DLLs into the consumer
        // project here so the source generator applied project-wide. The generator now ships
        // under the package's Runtime/Analyzers folder (Unity scopes it natively to the runtime
        // assembly and everything that references it, including the predefined Assembly-CSharp),
        // so this in-project copy is redundant and is removed on upgrade.
        internal const string LegacyAnalyzerCopyFolder =
            "Assets/Plugins/Editor/WallstopStudios.DxMessaging";

        private const string LegacySourceGeneratorDllName =
            "WallstopStudios.DxMessaging.SourceGenerators.dll";

        // Released 2.x legacy folders predate the companion analyzer DLL, so the source generator
        // is the required package-owned marker. Unknown DLLs still make the folder unsafe.
        private static readonly HashSet<string> RequiredLegacyAnalyzerCopyDlls = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            LegacySourceGeneratorDllName,
        };

        private static readonly HashSet<string> KnownLegacyAnalyzerCopyDlls = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            LegacySourceGeneratorDllName,
            "WallstopStudios.DxMessaging.Analyzer.dll",
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
            "System.Buffers.dll",
            "System.Collections.Immutable.dll",
            "System.Memory.dll",
            "System.Numerics.Vectors.dll",
            "System.Reflection.Metadata.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Text.Encoding.CodePages.dll",
            "System.Text.Encodings.Web.dll",
            "System.Threading.Tasks.Extensions.dll",
        };

        private static bool loggedSkippedLegacyAnalyzerCopyCleanup;

        static SetupCscRsp()
        {
            // Backstop only: the primary removal happens pre-compile in LegacyAnalyzerCopyCleanup.
            // This catches projects whose legacy copy predates the upgrade and triggers no import.
            ScheduleSetupStep(
                () => TryRemoveLegacyAnalyzerCopy(),
                "remove redundant in-project analyzer copy"
            );
            ScheduleAdditionalFileForIgnoreListSync();
        }

        /// <summary>
        /// Saves the settings and compiler inputs before a separate batch build or test process starts.
        /// </summary>
        /// <remarks>
        /// Call from an idle editor configuration entry point before changing player settings.
        /// Do not call from InitializeOnLoad, OnValidate, or other asset-import callbacks.
        /// This method writes and imports the ignore sidecar and response file synchronously;
        /// Unity can then compile the changed inputs. Let the configuration process finish before
        /// starting the build process. Preparation failures propagate to the caller, which must
        /// not report configuration success. Pending import retries last only within this domain.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The editor is not idle on its main thread.</exception>
        /// <example>
        /// <code>
        /// public static void ConfigureProject()
        /// {
        ///     DxMessaging.Editor.SetupCscRsp.PrepareCompilerInputs();
        ///     // Apply player settings in this configuration process, then exit it.
        /// }
        /// </code>
        /// </example>
        public static void PrepareCompilerInputs()
        {
            bool canPrepare =
                Thread.CurrentThread.ManagedThreadId == EditorThreadId
                && DxMessagingEditorIdle.CanMutateAssetDatabase()
                && !EditorApplication.isPlayingOrWillChangePlaymode
                && !BuildPipeline.isBuildingPlayer
                && !AssetDatabase.IsAssetImportWorkerProcess();
            PrepareCompilerInputs(
                canPrepare,
                () =>
                {
                    DxMessagingSettings settings = DxMessagingSettings.GetOrCreateSettings();
                    DxMessagingBaseCallIgnoreSync.RegenerateSidecarOrThrow(settings);
                },
                EnsureCscRsp,
                AssetDatabase.StartAssetEditing,
                AssetDatabase.StopAssetEditing,
                ref DxMessagingBaseCallIgnoreSync.SidecarImportPending,
                ref responseFileImportPending
            );
        }

        internal static void PrepareCompilerInputs(
            bool canPrepare,
            Action prepareSidecar,
            Action prepareResponseFile,
            Action startAssetEditing,
            Action stopAssetEditing,
            ref bool sidecarImportPending,
            ref bool rspImportPending
        )
        {
            if (!canPrepare)
            {
                throw new InvalidOperationException(
                    "PrepareCompilerInputs requires an idle main-thread editor configuration phase."
                );
            }
            try
            {
                startAssetEditing();
                Exception preparationFailure = null;
                try
                {
                    prepareSidecar();
                    prepareResponseFile();
                }
                catch (Exception ex)
                {
                    preparationFailure = ex;
                    throw;
                }
                finally
                {
                    try
                    {
                        stopAssetEditing();
                    }
                    catch (Exception stopFailure)
                    {
                        if (preparationFailure != null)
                        {
                            throw new AggregateException(
                                "Compiler input preparation and asset import completion both failed.",
                                preparationFailure,
                                stopFailure
                            );
                        }
                        throw;
                    }
                }
            }
            catch
            {
                // ImportAsset calls inside the batch only enqueue imports. StopAssetEditing can
                // fail after either file was written, so preserve both imports for an explicit retry.
                sidecarImportPending = true;
                rspImportPending = true;
                throw;
            }
        }

        internal static void ScheduleAdditionalFileForIgnoreListSync()
        {
            ScheduleSetupStep(EnsureCscRsp, "sync Assets/csc.rsp compiler options");
        }

        private static void ScheduleSetupStep(Action work, string description)
        {
            DxMessagingEditorIdle.ScheduleAssetDatabaseMutation(() =>
                RunSetupStep(work, description)
            );
        }

        private static void RunSetupStep(Action work, string description)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                DxMessagingEditorLog.LogError($"SetupCscRsp failed to {description}.", ex);
            }
        }

        /// <summary>
        /// Deletes the redundant in-project analyzer copy that older package versions deployed to
        /// <see cref="LegacyAnalyzerCopyFolder"/>. The source generator now ships under the
        /// package's <c>Runtime/Analyzers</c> folder and applies automatically, so the copy is no
        /// longer needed -- and, critically, leaving it in place makes BOTH copies generate into
        /// every DxMessaging-referencing assembly, emitting each member twice (CS0102). Removing it
        /// is therefore an upgrade requirement, not just cleanup.
        /// </summary>
        /// <remarks>
        /// Called from <see cref="LegacyAnalyzerCopyCleanup"/> (an <c>AssetPostprocessor</c>) so the
        /// removal lands during the asset import that PRECEDES script compilation -- before the two
        /// copies can both feed the compiler -- and again from the <c>[InitializeOnLoad]</c> static
        /// constructor as a post-compile backstop. Idempotent (a no-op once the folder is gone) and
        /// conservative: it leaves the folder untouched if it holds anything other than the analyzer
        /// DLLs this package deployed there. Returns <c>true</c> only when it actually deleted the
        /// folder.
        /// </remarks>
        internal static bool TryRemoveLegacyAnalyzerCopy()
        {
            if (!AssetDatabase.IsValidFolder(LegacyAnalyzerCopyFolder))
            {
                return false;
            }

            // The only shape this package ever created here is a flat set of analyzer / Roslyn
            // DLLs plus their auto-generated .meta sidecars. Inspect the on-disk folder and bail
            // out if a consumer repurposed it for anything else.
            string absoluteFolder = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", LegacyAnalyzerCopyFolder)
            );
            if (!Directory.Exists(absoluteFolder))
            {
                return false;
            }

            // A real subdirectory means a consumer repurposed this folder for their own content;
            // preserve it. The package only ever wrote a flat set of analyzer DLLs here, so the
            // safe-to-remove check below sees only files (a subfolder named "x.dll" can never be
            // mistaken for a DLL).
            string[] files = Directory.GetFiles(absoluteFolder);
            if (Directory.GetDirectories(absoluteFolder).Length > 0)
            {
                LogSkippedLegacyAnalyzerCopyCleanupIfNeeded(files);
                return false;
            }

            if (!IsLegacyAnalyzerCopySafeToRemove(files))
            {
                LogSkippedLegacyAnalyzerCopyCleanupIfNeeded(files);
                return false;
            }

            if (AssetDatabase.DeleteAsset(LegacyAnalyzerCopyFolder))
            {
                Debug.Log(
                    "DxMessaging: removed the redundant in-project analyzer copy at "
                        + LegacyAnalyzerCopyFolder
                        + ". The source generator now ships under the package's Runtime/Analyzers "
                        + "folder and applies automatically; nothing needs to live under Assets."
                );
                return true;
            }

            return false;
        }

        private static void LogSkippedLegacyAnalyzerCopyCleanupIfNeeded(IEnumerable<string> files)
        {
            if (
                loggedSkippedLegacyAnalyzerCopyCleanup
                || !ContainsKnownLegacyAnalyzerCopyEntry(files)
            )
            {
                return;
            }

            loggedSkippedLegacyAnalyzerCopyCleanup = true;
            Debug.LogWarning(
                "DxMessaging: found a legacy in-project analyzer copy at "
                    + LegacyAnalyzerCopyFolder
                    + " but did not remove it because the folder contains content outside "
                    + "the exact known package payload or is missing the required "
                    + "DxMessaging source-generator DLL. Move consumer-owned files out of that folder, "
                    + "then delete the stale DxMessaging analyzer DLLs manually to avoid "
                    + "double-loading analyzers."
            );
        }

        /// <summary>
        /// True when the legacy analyzer-copy folder contains the first-party source generator and
        /// every entry is one of the exact analyzer/dependency DLLs this package deployed there or
        /// a matching <c>.dll.meta</c> sidecar. A subfolder, a foreign DLL, or any other file makes
        /// this false so the folder is preserved rather than deleted.
        /// </summary>
        internal static bool IsLegacyAnalyzerCopySafeToRemove(IEnumerable<string> folderEntries)
        {
            HashSet<string> presentRequiredDlls = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenEntryNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in folderEntries ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                string name = GetLegacyAnalyzerCopyEntryName(entry);
                if (string.IsNullOrEmpty(name) || !seenEntryNames.Add(name))
                {
                    return false;
                }

                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (!KnownLegacyAnalyzerCopyDlls.Contains(name))
                    {
                        return false;
                    }

                    if (RequiredLegacyAnalyzerCopyDlls.Contains(name))
                    {
                        presentRequiredDlls.Add(name);
                    }

                    continue;
                }

                if (name.EndsWith(".dll.meta", StringComparison.OrdinalIgnoreCase))
                {
                    string dllName = name.Substring(0, name.Length - ".meta".Length);
                    if (!KnownLegacyAnalyzerCopyDlls.Contains(dllName))
                    {
                        return false;
                    }

                    continue;
                }

                return false;
            }

            return presentRequiredDlls.Count == RequiredLegacyAnalyzerCopyDlls.Count;
        }

        private static string GetLegacyAnalyzerCopyEntryName(string entry)
        {
            string normalizedEntry = entry.Replace("\\", "/");
            int lastSeparator = normalizedEntry.LastIndexOf('/');
            return lastSeparator >= 0
                ? normalizedEntry.Substring(lastSeparator + 1)
                : normalizedEntry;
        }

        private static bool ContainsKnownLegacyAnalyzerCopyEntry(IEnumerable<string> folderEntries)
        {
            foreach (string entry in folderEntries ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                string name = GetLegacyAnalyzerCopyEntryName(entry);
                if (KnownLegacyAnalyzerCopyDlls.Contains(name))
                {
                    return true;
                }

                if (!name.EndsWith(".dll.meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string dllName = name.Substring(0, name.Length - ".meta".Length);
                if (KnownLegacyAnalyzerCopyDlls.Contains(dllName))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Removes stale DxMessaging analyzer <c>-a:</c> entries from <c>csc.rsp</c>.
        /// </summary>
        /// <remarks>
        /// DxMessaging analyzers are activated solely through the RoslynAnalyzer-labeled
        /// DLLs shipped under the package's <c>Runtime/Analyzers</c> folder, which Unity
        /// scopes to the runtime assembly and every assembly that references it (including
        /// the predefined Assembly-CSharp). A stray <c>-a:</c> registration here (for
        /// example, one left behind by an older package version that copied analyzers into
        /// the project) double-loads the source generator, and registering dependency DLLs
        /// as analyzers makes Unity's compiler path fragile, so this method strips them.
        /// </remarks>
        private static void EnsureCscRsp()
        {
            SynchronizeResponseFiles(
                Application.dataPath,
                path => AssetDatabase.ImportAsset(path),
                ref responseFileImportPending
            );
        }

        internal static string GetRspFilePath(string assetsDirectory)
        {
            return Path.Combine(assetsDirectory, "csc.rsp").Replace("\\", "/");
        }

        internal static void SynchronizeResponseFiles(
            string assetsDirectory,
            Action<string> importAsset,
            ref bool importPending
        )
        {
            string rspPath = GetRspFilePath(assetsDirectory);
            string projectRoot = Path.GetFullPath(Path.Combine(assetsDirectory, ".."));
            string legacyRspPath = Path.Combine(projectRoot, "csc.rsp");
            string sidecarPath = DxMessagingBaseCallIgnoreSync.SidecarAssetPath;
            bool sidecarExists = File.Exists(Path.Combine(projectRoot, sidecarPath));

            string[] assetLines = ReadResponseFile(rspPath, out Encoding assetEncoding);
            string newline = FindNewline(assetLines);
            assetLines = CleanDxMessagingAnalyzerLines(assetLines, out bool removedAnalyzers);
            assetLines = SynchronizeAdditionalFileForIgnoreListLines(
                assetLines,
                sidecarPath,
                sidecarExists,
                out bool changedSidecar,
                newline
            );

            // Only remove package-managed options from the old root file. Moving unrelated
            // options to Assets would activate compiler settings Unity previously ignored.
            string[] legacyLines = ReadResponseFile(legacyRspPath, out Encoding legacyEncoding);
            legacyLines = CleanDxMessagingAnalyzerLines(
                legacyLines,
                out bool removedLegacyAnalyzers
            );
            legacyLines = SynchronizeAdditionalFileForIgnoreListLines(
                legacyLines,
                sidecarPath,
                false,
                out bool removedLegacySidecar
            );

            if (removedAnalyzers || changedSidecar)
            {
                WriteResponseFile(rspPath, assetLines, assetEncoding);
                importPending = true;
            }

            // Keep an unsuccessful import pending even if the file was already written. The
            // next scheduled sync must retry the import without rewriting unchanged contents.
            if (importPending)
            {
                importAsset(RspAssetPath);
                importPending = false;
            }

            // Do not retire the old wiring until the destination write and import succeeded.
            if (removedLegacyAnalyzers || removedLegacySidecar)
            {
                if (string.IsNullOrWhiteSpace(string.Concat(legacyLines)))
                {
                    File.Delete(legacyRspPath);
                }
                else
                {
                    WriteResponseFile(legacyRspPath, legacyLines, legacyEncoding);
                }
            }
        }

        private static string[] ReadResponseFile(string path, out Encoding encoding)
        {
            encoding = new UTF8Encoding(false, true);
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            using StreamReader reader = new(path, encoding, true);
            string text = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
            List<string> lines = new();
            int start = 0;
            for (int index = 0; index < text.Length; ++index)
            {
                if (text[index] != '\r' && text[index] != '\n')
                {
                    continue;
                }
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    ++index;
                }
                lines.Add(text.Substring(start, index + 1 - start));
                start = index + 1;
            }
            if (start < text.Length)
            {
                lines.Add(text.Substring(start));
            }
            return lines.ToArray();
        }

        private static string FindNewline(IEnumerable<string> lines)
        {
            foreach (string line in lines)
            {
                if (line.EndsWith("\r\n", StringComparison.Ordinal))
                {
                    return "\r\n";
                }
                if (line.EndsWith("\n", StringComparison.Ordinal))
                {
                    return "\n";
                }
                if (line.EndsWith("\r", StringComparison.Ordinal))
                {
                    return "\r";
                }
            }
            return Environment.NewLine;
        }

        private static void WriteResponseFile(string path, string[] lines, Encoding encoding)
        {
            WriteTextFileAtomically(path, string.Concat(lines), encoding);
        }

        internal static void WriteTextFileAtomically(string path, string content, Encoding encoding)
        {
            string temporaryPath = Path.Combine(
                Path.GetDirectoryName(path),
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"
            );
            try
            {
                File.WriteAllText(temporaryPath, content, encoding);
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        internal static string[] CleanDxMessagingAnalyzerLines(
            IEnumerable<string> lines,
            out bool foundStaleEntries
        )
        {
            List<string> result = new();
            foundStaleEntries = false;
            foreach (string line in lines ?? Array.Empty<string>())
            {
                string rewritten = RewriteResponseFileLine(
                    line,
                    argument =>
                    {
                        if (
                            !TryGetCompilerOptionValue(argument, "a", out string value)
                            && !TryGetCompilerOptionValue(argument, "analyzer", out value)
                        )
                        {
                            return argument;
                        }
                        return FilterCompilerPaths(
                            argument,
                            value,
                            path => !IsDxMessagingAnalyzerPath(DecodeCompilerPath(path))
                        );
                    }
                );
                foundStaleEntries |= !string.Equals(line, rewritten, StringComparison.Ordinal);
                if (rewritten != null)
                {
                    result.Add(rewritten);
                }
            }
            return result.ToArray();
        }

        internal static string[] SynchronizeAdditionalFileForIgnoreListLines(
            IEnumerable<string> lines,
            string sidecarRelativePath,
            bool sidecarExists,
            out bool modified,
            string newline = null
        )
        {
            string desired = FormatAdditionalFileArgument(sidecarRelativePath);
            List<string> result = new();
            bool foundDesired = false;
            modified = false;
            foreach (string line in lines ?? Array.Empty<string>())
            {
                string rewritten = RewriteResponseFileLine(
                    line,
                    argument =>
                    {
                        if (
                            !TryGetCompilerOptionValue(argument, "additionalfile", out string value)
                        )
                        {
                            return argument;
                        }
                        bool insertDesired = false;
                        string retained = FilterCompilerPaths(
                            argument,
                            value,
                            path =>
                            {
                                string decoded = DecodeCompilerPath(path).Replace("\\", "/");
                                string fileName = GetLegacyAnalyzerCopyEntryName(decoded);
                                if (
                                    !string.Equals(
                                        fileName,
                                        "DxMessaging.BaseCallIgnore.txt",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    && !string.Equals(
                                        fileName,
                                        "DxMessaging.BaseCallIgnore.generated.txt",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    return true;
                                }
                                if (
                                    sidecarExists
                                    && !foundDesired
                                    && string.Equals(
                                        decoded,
                                        sidecarRelativePath,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    foundDesired = true;
                                    insertDesired = true;
                                }
                                return false;
                            }
                        );
                        return insertDesired
                            ? (retained == null ? desired : retained + " " + desired)
                            : retained;
                    }
                );
                modified |= !string.Equals(line, rewritten, StringComparison.Ordinal);
                if (rewritten != null)
                {
                    result.Add(rewritten);
                }
            }
            if (sidecarExists && !foundDesired)
            {
                if (newline != null && result.Count > 0)
                {
                    string last = result[result.Count - 1];
                    if (
                        !last.EndsWith("\n", StringComparison.Ordinal)
                        && !last.EndsWith("\r", StringComparison.Ordinal)
                    )
                    {
                        result[result.Count - 1] += newline;
                    }
                }
                result.Add(desired + newline);
                modified = true;
            }
            return result.ToArray();
        }

        private static bool IsDxMessagingAnalyzerPath(string path)
        {
            string normalized = path.Replace("\\", "/");
            string fileName = GetLegacyAnalyzerCopyEntryName(normalized);
            if (
                string.Equals(
                    fileName,
                    LegacySourceGeneratorDllName,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    fileName,
                    "WallstopStudios.DxMessaging.Analyzer.dll",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return true;
            }
            if (!KnownLegacyAnalyzerCopyDlls.Contains(fileName))
            {
                return false;
            }
            foreach (string segment in normalized.Split('/'))
            {
                if (
                    string.Equals(
                        segment,
                        "com.wallstop-studios.dxmessaging",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || segment.StartsWith(
                        "com.wallstop-studios.dxmessaging@",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return true;
                }
            }
            return normalized.StartsWith(
                    LegacyAnalyzerCopyFolder + "/",
                    StringComparison.OrdinalIgnoreCase
                )
                || normalized.Contains(
                    "/" + LegacyAnalyzerCopyFolder + "/",
                    StringComparison.OrdinalIgnoreCase
                );
        }

        private static string FormatAdditionalFileArgument(string sidecarRelativePath)
        {
            return $"-additionalfile:\"{sidecarRelativePath}\"";
        }

        private static string FilterCompilerPaths(
            string argument,
            string value,
            Func<string, bool> retain
        )
        {
            StringBuilder kept = new();
            bool changed = false;
            bool inQuotes = false;
            int start = 0;
            // Roslyn splits analyzer/additionalfile path lists on comma and semicolon outside
            // quotes. Retain each surviving path and its original separator without decoding it.
            for (int index = 0; index <= value.Length; ++index)
            {
                if (index < value.Length && value[index] == '"')
                {
                    inQuotes = !inQuotes;
                }
                if (
                    index < value.Length
                    && (inQuotes || (value[index] != ',' && value[index] != ';'))
                )
                {
                    continue;
                }
                string path = value.Substring(start, index - start);
                if (retain(path))
                {
                    if (kept.Length > 0)
                    {
                        kept.Append(value[start - 1]);
                    }
                    kept.Append(path);
                }
                else
                {
                    changed = true;
                }
                start = index + 1;
            }
            if (!changed)
            {
                return argument;
            }
            if (kept.Length == 0)
            {
                return null;
            }
            string unquoted = UnquoteWholeArgument(argument);
            string rewritten = unquoted.Substring(0, unquoted.Length - value.Length) + kept;
            return unquoted == argument ? rewritten : "\"" + rewritten + "\"";
        }

        private static string RewriteResponseFileLine(string line, Func<string, string> rewrite)
        {
            if (line == null)
            {
                return null;
            }
            int length = line.Length;
            while (length > 0 && (line[length - 1] == '\r' || line[length - 1] == '\n'))
            {
                --length;
            }
            StringBuilder output = null;
            int copied = 0;
            int index = 0;
            while (index < length)
            {
                while (index < length && char.IsWhiteSpace(line[index]))
                {
                    ++index;
                }
                // Roslyn recognizes # at the beginning of a token. An embedded or quoted #
                // belongs to its argument and must not hide following compiler options.
                if (index == length || line[index] == '#')
                {
                    break;
                }
                int start = index;
                bool inQuotes = false;
                int backslashes = 0;
                while (index < length && (inQuotes || !char.IsWhiteSpace(line[index])))
                {
                    char character = line[index++];
                    if (character == '"' && backslashes % 2 == 0)
                    {
                        inQuotes = !inQuotes;
                    }
                    backslashes = character == '\\' ? backslashes + 1 : 0;
                }
                string argument = line.Substring(start, index - start);
                string replacement = rewrite(argument);
                if (string.Equals(argument, replacement, StringComparison.Ordinal))
                {
                    continue;
                }
                output ??= new StringBuilder();
                if (replacement == null)
                {
                    while (index < length && char.IsWhiteSpace(line[index]))
                    {
                        ++index;
                    }
                    if (index == length)
                    {
                        while (start > copied && char.IsWhiteSpace(line[start - 1]))
                        {
                            --start;
                        }
                    }
                }
                output.Append(line, copied, start - copied);
                output.Append(replacement);
                copied = index;
            }
            if (output == null)
            {
                return line;
            }
            output.Append(line, copied, line.Length - copied);
            string result = output.ToString();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static string DecodeCompilerPath(string path)
        {
            StringBuilder decoded = new();
            for (int index = 0; index < path.Length; ++index)
            {
                int slashes = 0;
                while (index < path.Length && path[index] == '\\')
                {
                    ++slashes;
                    ++index;
                }
                if (index < path.Length && path[index] == '"')
                {
                    decoded.Append('\\', slashes / 2);
                    if (slashes % 2 != 0)
                    {
                        decoded.Append('"');
                    }
                }
                else
                {
                    decoded.Append('\\', slashes);
                    if (index < path.Length)
                    {
                        decoded.Append(path[index]);
                    }
                }
            }
            return decoded.ToString();
        }

        private static bool TryGetCompilerOptionValue(
            string argument,
            string optionName,
            out string value
        )
        {
            value = string.Empty;
            if (string.IsNullOrEmpty(argument) || string.IsNullOrEmpty(optionName))
            {
                return false;
            }

            string unquotedArgument = UnquoteWholeArgument(argument.Trim());
            if (unquotedArgument.Length <= optionName.Length + 1)
            {
                return false;
            }

            if (unquotedArgument[0] != '-' && unquotedArgument[0] != '/')
            {
                return false;
            }

            if (unquotedArgument[optionName.Length + 1] != ':')
            {
                return false;
            }

            if (
                string.Compare(
                    unquotedArgument,
                    1,
                    optionName,
                    0,
                    optionName.Length,
                    StringComparison.OrdinalIgnoreCase
                ) != 0
            )
            {
                return false;
            }

            value = unquotedArgument.Substring(optionName.Length + 2);
            return true;
        }

        private static string UnquoteWholeArgument(string argument)
        {
            int quotes = 0;
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '"' && backslashes % 2 == 0)
                {
                    ++quotes;
                }
                backslashes = character == '\\' ? backslashes + 1 : 0;
            }
            if (quotes == 2 && argument[0] == '"' && argument[argument.Length - 1] == '"')
            {
                return argument.Substring(1, argument.Length - 2);
            }
            return argument;
        }
    }

    /// <summary>
    /// Removes the legacy in-project analyzer copy (via
    /// <see cref="SetupCscRsp.TryRemoveLegacyAnalyzerCopy"/>) during the asset import that PRECEDES
    /// script compilation. This is the primary upgrade path: deleting the redundant copy before the
    /// compiler runs prevents it and the package's Runtime/Analyzers copy from both generating into
    /// the same assembly (which would emit each member twice -- CS0102). It is a cheap no-op in the
    /// steady state: once the folder is gone, <c>AssetDatabase.IsValidFolder</c> short-circuits on
    /// every subsequent import.
    /// </summary>
    internal sealed class LegacyAnalyzerCopyCleanup : AssetPostprocessor
    {
        // Classic four-argument signature so this compiles on every supported Unity (2021.3+).
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths
        )
        {
            SetupCscRsp.TryRemoveLegacyAnalyzerCopy();
        }
    }
}
#endif
