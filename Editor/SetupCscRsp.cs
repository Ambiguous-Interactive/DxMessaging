#if UNITY_EDITOR

namespace DxMessaging.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using DxMessaging.Editor.Settings;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    [InitializeOnLoad]
    public static class SetupCscRsp
    {
        private static readonly string RspFilePath = Path.Combine(
                Application.dataPath,
                "..",
                "csc.rsp"
            )
            .Replace("\\", "/");

        private static readonly string[] AnalyzerDirectories =
        {
            "Packages/com.wallstop-studios.dxmessaging/Editor/Analyzers/",
            "Library/PackageCache/com.wallstop-studios.dxmessaging/Editor/Analyzers/",
        };

        private static readonly string SourceGeneratorDllName =
            "WallstopStudios.DxMessaging.SourceGenerators.dll";

        // The analyzer DLL is a SEPARATE assembly, but both compiler-host DLLs are pinned to
        // Unity 2021-compatible Roslyn 3.8.0. They ship side-by-side and both need the
        // RoslynAnalyzer label.
        private static readonly string AnalyzerDllName = "WallstopStudios.DxMessaging.Analyzer.dll";

        // The analyzer DLLs and shared Roslyn surface ship unconditionally; they're light enough
        // and required for DXMSG002–DXMSG009 to function at all. The list intentionally references
        // a few transitive Roslyn deps that may or may not physically ship with the package; the
        // copy loop below silently skips any name that isn't on disk.
        private static readonly string[] RequiredDllNames =
        {
            SourceGeneratorDllName,
            AnalyzerDllName,
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
            "System.Text.Encodings.Web.dll",
            "System.Reflection.Metadata.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Collections.Immutable.dll",
            "System.Memory.dll",
            "System.Buffers.dll",
            "System.Threading.Tasks.Extensions.dll",
            "System.Numerics.Vectors.dll",
            "System.Text.Encoding.CodePages.dll",
        };

        // DLLs that must be tagged with Unity's "RoslynAnalyzer" asset label so Unity's compiler
        // pipeline picks them up as analyzer/source-generator hosts. Other DLLs in the same folder
        // (Roslyn runtime, immutable collections) are plain Editor-only plugin DLLs.
        private static readonly HashSet<string> AnalyzerLabeledDllNames = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            SourceGeneratorDllName,
            AnalyzerDllName,
        };

        private static readonly HashSet<string> DllNames = new(StringComparer.OrdinalIgnoreCase);

        static SetupCscRsp()
        {
            ScheduleSetupStep(EnsureDLLsExistInAssets, "copy analyzer DLLs into Assets");
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

        private static void EnsureDLLsExistInAssets()
        {
            DllNames.Clear();
            foreach (
                string dllGuid in AssetDatabase.FindAssets("t:DefaultAsset", new[] { "Assets" })
            )
            {
                string dllPath = AssetDatabase.GUIDToAssetPath(dllGuid);
                if (!dllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!dllPath.Contains("Assets/Plugins", StringComparison.OrdinalIgnoreCase))
                {
                    string dllName = Path.GetFileName(dllPath);
                    DllNames.Add(dllName);
                }
            }

            foreach (string requiredDllName in RequiredDllNames)
            {
                if (DllNames.Contains(requiredDllName))
                {
                    continue;
                }

                foreach (string relativeDirectory in AnalyzerDirectories)
                {
                    try
                    {
                        string sourceFile = $"{relativeDirectory}{requiredDllName}";
                        if (!File.Exists(sourceFile))
                        {
                            continue;
                        }

                        const string pluginsDirectory =
                            "Assets/Plugins/Editor/WallstopStudios.DxMessaging/";
                        string outputAsset = $"{pluginsDirectory}{requiredDllName}";
                        if (!Directory.Exists(pluginsDirectory))
                        {
                            Directory.CreateDirectory(pluginsDirectory);
                            AssetDatabase.Refresh();
                        }
                        bool needsCopy = FilesDiffer(sourceFile, outputAsset);
                        if (needsCopy)
                        {
                            File.Copy(sourceFile, outputAsset, true);
                            AssetDatabase.ImportAsset(outputAsset);
                        }

                        if (AnalyzerLabeledDllNames.Contains(requiredDllName))
                        {
                            Object loadedDll = AssetDatabase.LoadMainAssetAtPath(outputAsset);
                            if (loadedDll != null)
                            {
                                string[] existingLabels = AssetDatabase.GetLabels(loadedDll);
                                if (!existingLabels.Contains("RoslynAnalyzer"))
                                {
                                    List<string> newLabels = existingLabels.ToList();
                                    newLabels.Add("RoslynAnalyzer");
                                    AssetDatabase.SetLabels(loadedDll, newLabels.ToArray());
                                }
                            }
                        }

                        if (AssetImporter.GetAtPath(outputAsset) is PluginImporter importer)
                        {
                            bool importerDirty = false;

                            // A RoslynAnalyzer-labeled DLL must be EXCLUDED from every
                            // build platform, including the Editor, so Unity treats it as a
                            // C# compiler analyzer rather than a managed precompiled
                            // assembly. The same-named DLL is importable from two locations
                            // (the package's own Editor/Analyzers copy and this Assets
                            // copy); if either is an Editor-enabled precompiled assembly,
                            // Unity 2021 aborts with "Multiple precompiled assemblies with
                            // the same name". The Roslyn runtime dependencies in the same
                            // folder already ship Editor-disabled and never collide -- this
                            // converges the analyzer DLLs onto that proven-safe shape.
                            // NOTE: SetExcludeFromAnyPlatform is a no-op once
                            // CompatibleWithAnyPlatform is false, so the Editor platform is
                            // disabled through the effective SetCompatibleWithEditor API.
                            if (importer.GetCompatibleWithAnyPlatform())
                            {
                                importer.SetCompatibleWithAnyPlatform(false);
                                importerDirty = true;
                            }

                            if (importer.GetCompatibleWithEditor())
                            {
                                importer.SetCompatibleWithEditor(false);
                                importerDirty = true;
                            }

                            if (importerDirty || needsCopy)
                            {
                                importer.SaveAndReimport();
                            }
                        }

                        DllNames.Add(requiredDllName);
                        break;
                    }
                    catch (Exception ex)
                    {
                        DxMessagingEditorLog.LogError(
                            $"Failed to copy {requiredDllName} to Assets.",
                            ex
                        );
                    }
                }
            }

            if (DllNames.Count > 0)
            {
                AssetDatabase.Refresh();
            }
        }

        private static bool FilesDiffer(string sourcePath, string destinationPath)
        {
            if (!File.Exists(destinationPath))
            {
                return true;
            }

            FileInfo sourceInfo = new(sourcePath);
            FileInfo destinationInfo = new(destinationPath);
            if (sourceInfo.Length != destinationInfo.Length)
            {
                return true;
            }

            using FileStream sourceStream = File.OpenRead(sourcePath);
            using FileStream destinationStream = File.OpenRead(destinationPath);
            using SHA256 sha256 = SHA256.Create();
            byte[] sourceHash = sha256.ComputeHash(sourceStream);
            byte[] destinationHash = sha256.ComputeHash(destinationStream);
            return !sourceHash.AsSpan().SequenceEqual(destinationHash);
        }

        /// <summary>
        /// Removes stale DxMessaging analyzer <c>-a:</c> entries from <c>csc.rsp</c>.
        /// </summary>
        /// <remarks>
        /// DxMessaging analyzers are activated solely through the RoslynAnalyzer-labeled
        /// <c>Assets/Plugins/Editor/WallstopStudios.DxMessaging</c> copy. A second
        /// <c>-a:</c> registration here double-loads the analyzer/source-generator, and
        /// registering dependency DLLs as analyzers makes Unity's compiler path fragile.
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
}
#endif
