#if UNITY_EDITOR

namespace DxMessaging.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using DxMessaging.Editor.Settings;
    using UnityEditor;
    using UnityEngine;

    [InitializeOnLoad]
    public static class SetupCscRsp
    {
        private static readonly string RspFilePath = Path.Combine(
                Application.dataPath,
                "..",
                "csc.rsp"
            )
            .Replace("\\", "/");

        // Older package versions copied the analyzer + Roslyn runtime DLLs into the consumer
        // project here so the source generator applied project-wide. The generator now ships
        // under the package's Runtime/Analyzers folder (Unity scopes it natively to the runtime
        // assembly and everything that references it, including the predefined Assembly-CSharp),
        // so this in-project copy is redundant and is removed on upgrade.
        internal const string LegacyAnalyzerCopyFolder =
            "Assets/Plugins/Editor/WallstopStudios.DxMessaging";

        static SetupCscRsp()
        {
            // Backstop only: the primary removal happens pre-compile in LegacyAnalyzerCopyCleanup.
            // This catches projects whose legacy copy predates the upgrade and triggers no import.
            ScheduleSetupStep(
                () => TryRemoveLegacyAnalyzerCopy(),
                "remove redundant in-project analyzer copy"
            );
            ScheduleSetupStep(EnsureCscRsp, "clean csc.rsp analyzer entries");
            ScheduleAdditionalFileForIgnoreListSync();
        }

        internal static void ScheduleAdditionalFileForIgnoreListSync()
        {
            ScheduleSetupStep(
                EnsureAdditionalFileForIgnoreList,
                "sync csc.rsp base-call ignore additionalfile entry"
            );
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
            if (Directory.GetDirectories(absoluteFolder).Length > 0)
            {
                return false;
            }

            if (!IsLegacyAnalyzerCopySafeToRemove(Directory.GetFiles(absoluteFolder)))
            {
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

        /// <summary>
        /// True when the legacy analyzer-copy folder is non-empty and every entry is a <c>.dll</c>
        /// or its <c>.dll.meta</c> sidecar (the exact shape this package deployed). A subfolder or
        /// any other file makes this false so the folder is preserved rather than deleted.
        /// </summary>
        internal static bool IsLegacyAnalyzerCopySafeToRemove(IEnumerable<string> folderEntries)
        {
            bool sawAnalyzerDll = false;
            foreach (string entry in folderEntries ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                string name = Path.GetFileName(entry);
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    sawAnalyzerDll = true;
                    continue;
                }

                if (name.EndsWith(".dll.meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return false;
            }

            return sawAnalyzerDll;
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
            try
            {
                if (!File.Exists(RspFilePath))
                {
                    File.WriteAllText(RspFilePath, string.Empty);
                    AssetDatabase.ImportAsset("csc.rsp");
                }

                string rspContent = File.ReadAllText(RspFilePath);

                string[] newLines = CleanDxMessagingAnalyzerLines(
                    rspContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                    out bool foundStaleEntries
                );

                if (foundStaleEntries)
                {
                    // Write the cleaned up content
                    string newContent = string.Join(Environment.NewLine, newLines);
                    if (!string.IsNullOrEmpty(newContent))
                    {
                        newContent += Environment.NewLine;
                    }
                    File.WriteAllText(RspFilePath, newContent);
                    AssetDatabase.ImportAsset("csc.rsp");
                    Debug.Log("Updated csc.rsp.");
                }
            }
            catch (IOException ex)
            {
                DxMessagingEditorLog.LogError("Failed to modify csc.rsp.", ex);
            }
        }

        internal static string[] CleanDxMessagingAnalyzerLines(
            IEnumerable<string> lines,
            out bool foundStaleEntries
        )
        {
            List<string> newLines = new();
            foundStaleEntries = false;

            foreach (string line in lines ?? Array.Empty<string>())
            {
                string trimmedLine = line?.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                {
                    continue;
                }

                if (IsResponseFileComment(trimmedLine))
                {
                    newLines.Add(trimmedLine);
                    continue;
                }

                List<string> retainedArguments = new();
                bool removedFromLine = false;
                foreach (string argument in SplitResponseFileArguments(trimmedLine))
                {
                    if (IsDxMessagingAnalyzerArgument(argument))
                    {
                        foundStaleEntries = true;
                        removedFromLine = true;
                        continue;
                    }

                    retainedArguments.Add(argument);
                }

                if (removedFromLine)
                {
                    if (retainedArguments.Count > 0)
                    {
                        newLines.Add(string.Join(" ", retainedArguments));
                    }
                    continue;
                }

                newLines.Add(trimmedLine);
            }

            return newLines.ToArray();
        }

        private static bool IsDxMessagingAnalyzerArgument(string trimmedLine)
        {
            return (
                    TryGetCompilerOptionValue(trimmedLine, "a", out string _)
                    || TryGetCompilerOptionValue(trimmedLine, "analyzer", out string _)
                )
                && (
                    trimmedLine.Contains(
                        "com.wallstop-studios.dxmessaging",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || trimmedLine.Contains(
                        "WallstopStudios.DxMessaging",
                        StringComparison.OrdinalIgnoreCase
                    )
                );
        }

        /// <summary>
        /// Ensures <c>csc.rsp</c> contains a single <c>-additionalfile:</c> line pointing at the
        /// base-call ignore sidecar, when (and only when) that sidecar physically exists. Stale
        /// entries pointing at moved or deleted sidecar paths are removed.
        /// </summary>
        /// <remarks>
        /// The sidecar is generated by <see cref="DxMessagingBaseCallIgnoreSync"/>. csc happily
        /// runs without it, so this method does NOT auto-create; sidecar writes schedule this sync
        /// again after deferred regeneration completes.
        /// </remarks>
        private static void EnsureAdditionalFileForIgnoreList()
        {
            try
            {
                if (!File.Exists(RspFilePath))
                {
                    File.WriteAllText(RspFilePath, string.Empty);
                    AssetDatabase.ImportAsset("csc.rsp");
                }

                string sidecarRelativePath = DxMessagingBaseCallIgnoreSync.SidecarAssetPath;
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                    .Replace("\\", "/");
                string sidecarAbsolutePath = Path.Combine(projectRoot, sidecarRelativePath)
                    .Replace("\\", "/");

                bool sidecarExists = File.Exists(sidecarAbsolutePath);
                string[] newLines = SynchronizeAdditionalFileForIgnoreListLines(
                    File.ReadAllLines(RspFilePath),
                    sidecarRelativePath,
                    sidecarExists,
                    out bool modified
                );

                if (modified)
                {
                    string newContent = string.Join(Environment.NewLine, newLines);
                    if (!string.IsNullOrEmpty(newContent))
                    {
                        newContent += Environment.NewLine;
                    }
                    File.WriteAllText(RspFilePath, newContent);
                    AssetDatabase.ImportAsset("csc.rsp");
                    Debug.Log("Updated csc.rsp additionalfile entries.");
                }
            }
            catch (IOException ex)
            {
                DxMessagingEditorLog.LogError("Failed to update csc.rsp additionalfile entry.", ex);
            }
        }

        internal static string[] SynchronizeAdditionalFileForIgnoreListLines(
            IEnumerable<string> lines,
            string sidecarRelativePath,
            bool sidecarExists,
            out bool modified
        )
        {
            string desiredLine = FormatAdditionalFileArgument(sidecarRelativePath);
            List<string> newLines = new();
            bool foundDesired = false;
            bool foundStale = false;

            foreach (string line in lines ?? Array.Empty<string>())
            {
                string trimmedLine = line?.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                {
                    continue;
                }

                if (IsResponseFileComment(trimmedLine))
                {
                    newLines.Add(trimmedLine);
                    continue;
                }

                List<string> retainedArguments = new();
                bool removedFromLine = false;
                foreach (string argument in SplitResponseFileArguments(trimmedLine))
                {
                    if (!IsDxMessagingBaseCallIgnoreAdditionalFile(argument))
                    {
                        retainedArguments.Add(argument);
                        continue;
                    }

                    bool isDesired =
                        sidecarExists
                        && IsAdditionalFileArgumentForPath(argument, sidecarRelativePath);
                    if (!isDesired)
                    {
                        // Stale entry pointing at a moved/renamed/deleted sidecar; drop it.
                        foundStale = true;
                        removedFromLine = true;
                        continue;
                    }

                    if (foundDesired)
                    {
                        // Drop duplicate.
                        foundStale = true;
                        removedFromLine = true;
                        continue;
                    }

                    retainedArguments.Add(desiredLine);
                    foundDesired = true;
                    if (!string.Equals(argument, desiredLine, StringComparison.Ordinal))
                    {
                        foundStale = true;
                        removedFromLine = true;
                    }
                }

                if (retainedArguments.Count > 0)
                {
                    newLines.Add(string.Join(" ", retainedArguments));
                    continue;
                }

                if (!removedFromLine)
                {
                    newLines.Add(trimmedLine);
                }
            }

            bool needsAppend = sidecarExists && !foundDesired;
            if (needsAppend)
            {
                newLines.Add(desiredLine);
            }

            modified = foundStale || needsAppend;
            return newLines.ToArray();
        }

        private static string FormatAdditionalFileArgument(string sidecarRelativePath)
        {
            return $"-additionalfile:\"{sidecarRelativePath}\"";
        }

        private static bool IsResponseFileComment(string trimmedLine)
        {
            return trimmedLine.StartsWith("#", StringComparison.Ordinal);
        }

        private static bool IsDxMessagingBaseCallIgnoreAdditionalFile(string trimmedLine)
        {
            return TryGetCompilerOptionValue(trimmedLine, "additionalfile", out string value)
                && value.Contains("DxMessaging.", StringComparison.OrdinalIgnoreCase)
                && value.Contains("BaseCallIgnore", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAdditionalFileArgumentForPath(
            string argument,
            string sidecarRelativePath
        )
        {
            if (!TryGetCompilerOptionValue(argument, "additionalfile", out string value))
            {
                return false;
            }

            string unquotedValue = UnquoteWholeArgument(value.Trim());
            return string.Equals(
                unquotedValue.Replace("\\", "/"),
                sidecarRelativePath,
                StringComparison.OrdinalIgnoreCase
            );
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
            if (argument.Length >= 2 && argument[0] == '"' && argument[argument.Length - 1] == '"')
            {
                return argument.Substring(1, argument.Length - 2).Replace("\"\"", "\"");
            }

            return argument;
        }

        private static List<string> SplitResponseFileArguments(string line)
        {
            List<string> arguments = new();
            if (string.IsNullOrWhiteSpace(line))
            {
                return arguments;
            }

            System.Text.StringBuilder current = new();
            bool inQuotes = false;
            foreach (char character in line)
            {
                if (character == '"')
                {
                    inQuotes = !inQuotes;
                    current.Append(character);
                    continue;
                }

                if (char.IsWhiteSpace(character) && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        arguments.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(character);
            }

            if (current.Length > 0)
            {
                arguments.Add(current.ToString());
            }

            return arguments;
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
