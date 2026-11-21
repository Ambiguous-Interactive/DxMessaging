namespace DxMessaging.Editor
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using UnityEditor;
    using UnityEditor.Compilation;
    using UnityEngine;

    /// <summary>
    /// Upgrades 3.x fast-handler callbacks and inherited string-handler opt-ins for 4.0.
    /// </summary>
    internal static class ReadonlyFastHandlerUpgrade
    {
        internal const string MenuPath =
            "Tools/Wallstop Studios/DxMessaging/Upgrade 3.x Fast Handlers to 4.0";

        private static readonly HashSet<string> ReadonlyRegistrationNames = new(
            StringComparer.Ordinal
        )
        {
            "RegisterBroadcast",
            "RegisterBroadcastPostProcessor",
            "RegisterBroadcastWithoutSource",
            "RegisterBroadcastWithoutSourcePostProcessor",
            "RegisterComponentBroadcast",
            "RegisterComponentBroadcastPostProcessor",
            "RegisterComponentTargeted",
            "RegisterComponentTargetedPostProcessor",
            "RegisterGameObjectBroadcast",
            "RegisterGameObjectBroadcastPostProcessor",
            "RegisterGameObjectTargeted",
            "RegisterGameObjectTargetedPostProcessor",
            "RegisterGlobalAcceptAll",
            "RegisterTargeted",
            "RegisterTargetedPostProcessor",
            "RegisterTargetedWithoutTargeting",
            "RegisterTargetedWithoutTargetingPostProcessor",
            "RegisterUntargeted",
            "RegisterUntargetedPostProcessor",
        };

        private static readonly HashSet<string> MutableRegistrationNames = new(
            StringComparer.Ordinal
        )
        {
            "RegisterBroadcastInterceptor",
            "RegisterTargetedInterceptor",
            "RegisterUntargetedInterceptor",
        };

        private static readonly HashSet<string> ChangedOverrideNames = new(StringComparer.Ordinal)
        {
            "HandleGlobalStringMessage",
            "HandleStringComponentMessage",
            "HandleStringGameObjectMessage",
        };

        private static readonly Regex IdentifierRegex = new(
            @"^(?:this\s*\.\s*)?(?<name>[A-Za-z_]\w*)$",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex NamedIdentifierRegex = new(
            @"^(?:\(\s*(?:(?:global::)?DxMessaging\s*\.\s*Core\s*\.\s*)?MessageHandler\s*\.\s*FastHandler(?:WithContext)?\s*<[^()]+>\s*\)\s*)?(?:this\s*\.\s*)?(?<name>[A-Za-z_]\w*)$",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex ConstructedHandlerRegex = new(
            @"^new\s+(?:(?:global::)?DxMessaging\s*\.\s*Core\s*\.\s*)?MessageHandler\s*\.\s*FastHandler(?:WithContext)?\s*<[^()]+>\s*\(\s*(?<name>[A-Za-z_]\w*)\s*\)$",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex RefRegex = new(
            @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])ref(?![\w\p{Nl}\p{Mc}\p{Cf}\\])",
            RegexOptions.CultureInvariant
        );

        private const string CSharpLineTerminators = "\r\n\u0085\u2028\u2029";

        private const string UnicodeEscapePattern = @"\\(?:u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})";

        private const string IdentifierSpellingPattern =
            @"@?(?:[_\p{L}\p{Nl}]|"
            + UnicodeEscapePattern
            + @")(?:[\p{L}\p{Nl}\p{Nd}\p{Pc}\p{Mn}\p{Mc}\p{Cf}]|"
            + UnicodeEscapePattern
            + ")*";

        private static readonly Regex IdentifierUseRegex = new(
            @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])" + IdentifierSpellingPattern,
            RegexOptions.CultureInvariant
        );

        private static readonly Regex DirectMemberIdentifierRegex = new(
            @"\G" + IdentifierSpellingPattern,
            RegexOptions.CultureInvariant
        );

        private static readonly Regex ValueAccessorRegex = new(
            @"(?<![\w@])(?:set|init|add|remove)\s*(?<body>\{|=>)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private const string ReceiverDeclarationName =
            @"\s+(?<name>" + IdentifierSpellingPattern + ")";

        private static readonly Regex PackageReceiverDeclarationRegex = new(
            @"(?<qualified>(?<global>global\s*::\s*)?DxMessaging\s*\.\s*Core\s*\.\s*)?(?<![\w@\p{Nl}\p{Mc}\p{Cf}])@?MessageRegistrationToken(?:\s*\?)?"
                + ReceiverDeclarationName,
            RegexOptions.CultureInvariant
        );

        private static readonly Regex ReceiverDeclarationRegex = new(
            @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])(?=(?<type>"
                + IdentifierSpellingPattern
                + ")"
                + TypeArgumentListPattern
                + @"(?:\s*[?*]|\s*\[[,\s]*\])*"
                + ReceiverDeclarationName
                + ")",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex NamespaceAliasRegex = new(
            @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])using\s+(?<alias>" + IdentifierSpellingPattern + @")\s*=",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private const string SimpleDeclaredTypePattern =
            IdentifierSpellingPattern
            + @"(?:\s*(?:\.|::)\s*"
            + IdentifierSpellingPattern
            + @")*(?:\s*[?*]|\s*\[[,\s]*\])*";

        private const string TypeArgumentCharacters = @"[\w\p{Nl}\p{Mc}\p{Cf}\s.,:<>?*\[\]()@\\]+";
        private const string TypeArgumentListPattern = @"(?:\s*<" + TypeArgumentCharacters + ">)?";

        private const string CastTypeNamePattern =
            IdentifierSpellingPattern + TypeArgumentListPattern;

        private const string CastTypeSuffixPattern =
            @"(?:\s*[?*]|\s*\[[,\s]*\])*(?:\s+(?<elementName>"
            + IdentifierSpellingPattern
            + @"))?\s*\z";

        private const string FunctionPointerTypePattern =
            @"delegate\s*\*\s*(?:(?:managed|unmanaged)(?:\s*\[[^\]]*\])?\s*)?<"
            + TypeArgumentCharacters
            + ">";

        private const string MemberTypePattern =
            "(?:"
            + FunctionPointerTypePattern
            + "|"
            + CastTypeNamePattern
            + @"(?:\s*(?:\.|::)\s*"
            + CastTypeNamePattern
            + ")*"
            + @"|\([^;={}]+\))(?:\s*[?*]|\s*\[[,\s]*\])*";

        private static readonly Regex DeclaredCallbackTypeRegex = new(
            @"\A" + MemberTypePattern + @"\z",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex DecimalIntLiteralRegex = new(
            @"\A[+-]?[0-9](?:[0-9_]*[0-9])?\z",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex DirectMemberDeclarationRegex = new(
            @"\G(?:(?<modifier>private|protected|internal|public|new|override|virtual|abstract|sealed|static|readonly|required|unsafe|extern|partial|ref|volatile|const|async)\s+)*"
                + @"(?:(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+|"
                + @"(?:(?:delegate|event)\s+)?"
                + MemberTypePattern
                + @"\s+)"
                + "(?<name>"
                + IdentifierSpellingPattern
                + ")",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex InvocationTargetRegex = new(
            @"\A"
                + CastTypeNamePattern
                + @"(?:\s*(?:\??\.|::)\s*"
                + CastTypeNamePattern
                + @")*\s*\z",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex GroupedCastTypeRegex = new(
            @"\G\s*"
                + CastTypeNamePattern
                + @"(?:\s*(?:\.|::)\s*"
                + CastTypeNamePattern
                + ")*"
                + CastTypeSuffixPattern,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex GroupedCastTupleSuffixRegex = new(
            @"\G" + CastTypeSuffixPattern,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex LocalFunctionDeclarationRegex = new(
            @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])(?:(?:static|async|unsafe|ref|readonly)\s+)*"
                + SimpleDeclaredTypePattern
                + @"\s+"
                + IdentifierSpellingPattern
                + @"\s*\(",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex LocalFunctionParameterRegex = new(
            @"\G\s*(?<modifiers>(?:(?:scoped|ref|readonly|in|out|params)\s+)*)(?<type>"
                + SimpleDeclaredTypePattern
                + @")\s+(?<name>"
                + IdentifierSpellingPattern
                + @")\s*\z",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex ByReferenceModifierRegex = new(
            @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])(?<modifier>ref|out)(?=\s|[(@]|/\*)",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex ByReferenceOperandRegex = new(
            @"\G(?<grouping>(?:(?:(?:checked|unchecked)\s*)?\(\s*)*)(?<name>"
                + IdentifierSpellingPattern
                + ")",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex ReadonlyRefLocalPrefixRegex = new(
            @"\G\s*ref\s+readonly\s+"
                + IdentifierSpellingPattern
                + @"(?:\s*(?:\.|::)\s*"
                + IdentifierSpellingPattern
                + @")*\s+"
                + IdentifierSpellingPattern
                + @"\s*=\s*",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex BaseCallRegex = new(
            @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])base\s*\.\s*(?<member>"
                + IdentifierSpellingPattern
                + @")\s*\(",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex BaseRefArgumentRegex = new(
            @"^\s*ref(?:\s+|(?=@))(?<name>" + IdentifierSpellingPattern + @")\s*$",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex UnicodeEscapeRegex = new(
            UnicodeEscapePattern,
            RegexOptions.CultureInvariant
        );

        private static readonly Regex IdentifierFormattingRegex = new(
            @"\p{Cf}",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex DirectWriteOperatorRegex = new(
            @"\G(?:\+\+|--|(?:<<|>>>?|[+*/%&|^\-]|\?\?)=|=(?!=|>))",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex TupleControlKeywordRegex = new(
            @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])(?:(?<condition>if|while|for|foreach|lock|using|fixed)|else|do)\z",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex NamespaceDeclarationRegex = new(
            @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])namespace\s+(?<path>"
                + IdentifierSpellingPattern
                + @"(?:\s*\.\s*"
                + IdentifierSpellingPattern
                + @")*)\s*(?:[;{])",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex TypeDeclarationRegex = new(
            @"(?<![\w@])(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>"
                + IdentifierSpellingPattern
                + @")(?:\s*<[^>{};]+>)?",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex StaticImportRegex = new(
            @"(?<![\w@])using\s+static\b",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        private static readonly Regex DelegateTypeDeclarationRegex = new(
            @"(?<![\w@])(?<delegate>delegate)\s+(?:ref\s+(?:readonly\s+)?)?"
                + MemberTypePattern
                + @"\s+(?<name>"
                + IdentifierSpellingPattern
                + @")(?:\s*<[^>{};()]+>)?\s*\(",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );

        [MenuItem(MenuPath)]
        private static void UpgradeProject()
        {
            List<FileUpgrade> upgrades = new();
            SortedSet<string> manualReview = new(StringComparer.Ordinal);
            HashSet<string> importedScripts = GetImportedScriptPaths();
            string assetsRoot;
            string[] assetPaths;
            try
            {
                assetsRoot =
                    Path.GetFullPath(Application.dataPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                assetPaths = GetProjectFilePaths(assetsRoot, importedScripts);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                DxMessagingEditorLog.LogWarning(
                    "Could not enumerate C# scripts and assembly settings under Assets.",
                    exception
                );
                EditorUtility.DisplayDialog(
                    "DxMessaging Fast Handler Upgrade",
                    "The preview needs to read the C# scripts and assembly settings under Assets. See the Console for details.",
                    "OK"
                );
                return;
            }

            Dictionary<string, byte[]> originalFiles = new(StringComparer.Ordinal);
            bool assemblySettingsAreCurrent = true;
            Dictionary<string, string> scriptAssemblies = importedScripts.ToDictionary(
                path => path,
                CompilationPipeline.GetAssemblyNameFromScriptPath,
                StringComparer.Ordinal
            );
            Dictionary<string, (string FullPath, EncodedSource Source)> readableScripts = new(
                StringComparer.Ordinal
            );
            ProjectCallbackUses projectUses = new();

            foreach (string assetPath in assetPaths)
            {
                bool isScript = IsScriptPath(assetPath);
                try
                {
                    string fullPath = GetProjectFileFullPath(assetsRoot, assetPath);
                    byte[] originalBytes = File.ReadAllBytes(fullPath);
                    originalFiles.Add(assetPath, originalBytes);
                    if (!isScript)
                    {
                        continue;
                    }

                    EncodedSource source = EncodedSource.FromBytes(originalBytes);
                    scriptAssemblies.TryGetValue(assetPath, out string assemblyName);
                    projectUses.AddScript(assetPath, source.Text, assemblyName);
                    if (!IsGeneratedPath(assetPath) && !IsGeneratedSource(assetPath, source.Text))
                    {
                        readableScripts.Add(assetPath, (fullPath, source));
                    }
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    originalFiles.TryAdd(assetPath, null);
                    if (isScript)
                    {
                        projectUses.MarkUnreadable(assetPath);
                        manualReview.Add(
                            $"{assetPath}: this script could not be read. {exception.Message}"
                        );
                    }
                    else
                    {
                        assemblySettingsAreCurrent = false;
                        manualReview.Add(
                            $"{assetPath}: assembly settings could not be read. {exception.Message}"
                        );
                    }
                }
                catch (DecoderFallbackException)
                {
                    projectUses.MarkUnreadable(assetPath);
                    manualReview.Add(
                        $"{assetPath}: encoding is not UTF-8, UTF-16, or UTF-32; file was not read"
                    );
                }
                catch (RegexMatchTimeoutException)
                {
                    projectUses.MarkUnreadable(assetPath);
                    manualReview.Add(
                        $"{assetPath}: the preview could not finish scanning this file; inspect its callbacks manually"
                    );
                }
            }

            foreach (string assetPath in assetPaths.Where(IsAssemblySettingsPath))
            {
                string mismatch = FindAssemblySettingsMismatch(assetPath, originalFiles);
                if (mismatch != null)
                {
                    assemblySettingsAreCurrent = false;
                    manualReview.Add(
                        $"{mismatch} Refresh and preview again to check routes across scripts."
                    );
                }
            }

            ProjectPackageBindings packageBindings = new(
                assetsRoot,
                scriptAssemblies,
                originalFiles
            );
            foreach ((string assetPath, (string fullPath, EncodedSource source)) in readableScripts)
            {
                bool isImported = scriptAssemblies.TryGetValue(assetPath, out string assemblyName);
                UpgradeResult result;
                try
                {
                    result = Analyze(
                        source.Text,
                        name => projectUses.FindOtherUse(assetPath, name),
                        projectUses,
                        assemblySettingsAreCurrent ? assemblyName : null,
                        projectSourcesLimitedToAssets: true,
                        findPackageBindingIssue: type =>
                            assemblySettingsAreCurrent
                                ? packageBindings.FindIssue(assetPath, type)
                                : "Unity's imported assembly settings do not match disk"
                    );
                }
                catch (RegexMatchTimeoutException)
                {
                    manualReview.Add(
                        $"{assetPath}: the preview could not finish scanning this file; inspect its callbacks manually"
                    );
                    continue;
                }

                foreach (string skippedMethod in result.ManualReviewMethods)
                {
                    manualReview.Add($"{assetPath}: {skippedMethod}");
                }

                bool proposesEdits =
                    0 < result.ReplacementCount || 0 < result.StringHandlerOptInCount;
                if (!isImported)
                {
                    if (proposesEdits || 0 < result.ManualReviewMethods.Count)
                    {
                        manualReview.Add(
                            $"{assetPath}: Unity has not imported this script. Refresh the Asset Database and preview again to check its callbacks."
                        );
                    }
                    continue;
                }
                if (!proposesEdits)
                {
                    continue;
                }

                upgrades.Add(
                    new FileUpgrade(
                        fullPath,
                        originalFiles[assetPath],
                        source.Encode(result.UpgradedSource),
                        result.ReplacementCount,
                        result.StringHandlerOptInCount
                    )
                );
            }

            int replacementCount = 0;
            int stringHandlerOptInCount = 0;
            foreach (FileUpgrade upgrade in upgrades)
            {
                replacementCount += upgrade.ReplacementCount;
                stringHandlerOptInCount += upgrade.StringHandlerOptInCount;
            }

            if (replacementCount == 0 && stringHandlerOptInCount == 0)
            {
                string message =
                    manualReview.Count == 0
                        ? "No 3.x fast handlers or inherited string handlers were found under Assets."
                        : $"No automatic edits were found under Assets.\n\n{manualReview.Count} item(s) require manual review. "
                            + "See the Console for details.";
                EditorUtility.DisplayDialog("DxMessaging Fast Handler Upgrade", message, "OK");
                LogManualReview(manualReview);
                return;
            }

            string manualReviewSummary =
                manualReview.Count == 0
                    ? string.Empty
                    : $"\n\n{manualReview.Count} item(s) will be left unchanged and "
                        + "reported in the Console.";
            bool confirmed = EditorUtility.DisplayDialog(
                "DxMessaging Fast Handler Upgrade",
                $"Update {replacementCount} parameter(s) and add {stringHandlerOptInCount} "
                    + $"string-handler opt-in(s) in {upgrades.Count} script(s)?\n\n"
                    + "Only Unity-imported scripts under Assets are changed. Interceptors and emission calls remain "
                    + "writable by ref. Earlier writes are restored if a later write fails; newer "
                    + "concurrent edits are preserved and reported."
                    + manualReviewSummary,
                "Upgrade",
                "Cancel"
            );
            if (!confirmed)
            {
                return;
            }

            try
            {
                VerifyProjectFileSnapshot(
                    assetsRoot,
                    originalFiles,
                    scriptAssemblies,
                    assemblySettingsAreCurrent
                );
                packageBindings.Verify();
                Dictionary<string, byte[]> expectedWrites = upgrades.ToDictionary(
                    upgrade => upgrade.FullPath,
                    upgrade => upgrade.UpgradedBytes,
                    StringComparer.Ordinal
                );
                ApplyAtomically(upgrades, assetsRoot, () =>
                {
                    VerifyProjectFileSnapshot(
                        assetsRoot,
                        originalFiles,
                        scriptAssemblies,
                        assemblySettingsAreCurrent,
                        expectedWrites
                    );
                    packageBindings.Verify();
                });
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog(
                    "DxMessaging Fast Handler Upgrade Failed",
                    "The upgrade could not be completed. Completed writes were rolled back. If "
                        + "a restore also failed, its backup was preserved beside the script. See "
                        + "the Console for details.",
                    "OK"
                );
                return;
            }

            try
            {
                LogManualReview(manualReview);
                AssetDatabase.Refresh();
                Debug.Log(
                    $"[DxMessaging] Upgraded {replacementCount} fast-handler parameter(s) and "
                        + $"added {stringHandlerOptInCount} string-handler opt-in(s) in "
                        + $"{upgrades.Count} script(s). Review the diff before committing."
                );
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog(
                    "DxMessaging Scripts Updated",
                    "The scripts were updated, but Unity could not refresh the Asset Database. "
                        + "Refresh it manually and review the diff before continuing.",
                    "OK"
                );
            }
        }

        private static HashSet<string> GetImportedScriptPaths() =>
            GetImportedPaths("t:MonoScript").Where(IsScriptPath).ToHashSet(StringComparer.Ordinal);

        private static IEnumerable<string> GetImportedPaths(
            string filter,
            string root = "Assets"
        ) =>
            AssetDatabase
                .FindAssets(filter, root == null ? null : new[] { root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path =>
                    !string.IsNullOrEmpty(path) && !path.Split('/').Any(IsUnityIgnoredName)
                );

        private static bool IsScriptPath(string path) =>
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        private static bool IsAssemblySettingsPath(string path) =>
            path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase);

        private static bool IsAssemblySettingsMetadata(string path) =>
            path.EndsWith(".asmdef.meta", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".asmref.meta", StringComparison.OrdinalIgnoreCase);

        private static string[] GetProjectFilePaths(
            string assetsRoot,
            HashSet<string> importedScripts
        )
        {
            // The imported inventory can lag additions, removals and changes to disk.
            IEnumerable<string> diskPaths = EnumerateProjectFiles(assetsRoot)
                .Select(path =>
                    "Assets/"
                    + Path.GetRelativePath(assetsRoot, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                );
            IEnumerable<string> importedSettings = GetImportedPaths("t:AssemblyDefinitionAsset")
                .Concat(GetImportedPaths("t:AssemblyDefinitionReferenceAsset"))
                .Where(IsAssemblySettingsPath);
            return importedScripts
                .Union(importedSettings)
                .Union(diskPaths)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        private static IEnumerable<string> EnumerateProjectFiles(string assetsRoot)
        {
            EnumerationOptions options = new()
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            };
            Stack<string> directories = new();
            directories.Push(assetsRoot);
            while (directories.TryPop(out string directory))
            {
                ValidateProjectPath(assetsRoot, directory);
                foreach (string child in Directory.EnumerateDirectories(directory, "*", options))
                {
                    if (
                        !IsUnityIgnoredName(Path.GetFileName(child))
                        && (File.GetAttributes(child) & FileAttributes.Hidden) == 0
                    )
                    {
                        directories.Push(child);
                    }
                }
                foreach (string path in Directory.EnumerateFiles(directory, "*", options))
                {
                    if (
                        !IsUnityIgnoredName(Path.GetFileName(path))
                        && (
                            IsScriptPath(path)
                            || IsAssemblySettingsPath(path)
                            || IsAssemblySettingsMetadata(path)
                        )
                    )
                    {
                        yield return path;
                    }
                }
            }
        }

        private static bool IsUnityIgnoredName(string name) =>
            name.StartsWith(".", StringComparison.Ordinal)
            || name.EndsWith("~", StringComparison.Ordinal)
            || name == "cvs";

        private static string GetProjectFileFullPath(string assetsRoot, string assetPath)
        {
            const string AssetPrefix = "Assets/";
            if (!assetPath.StartsWith(AssetPrefix, StringComparison.Ordinal))
            {
                throw new IOException($"The project file '{assetPath}' is outside Assets.");
            }

            string path = Path.GetFullPath(
                Path.Combine(assetsRoot, assetPath.Substring(AssetPrefix.Length))
            );
            ValidateProjectPath(assetsRoot, path);
            return path;
        }

        private static void ValidateProjectPath(string assetsRoot, string fullPath)
        {
            StringComparison comparison =
                Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
            if (!fullPath.StartsWith(assetsRoot, comparison))
            {
                throw new IOException($"The project file '{fullPath}' is outside Assets.");
            }

            string current = assetsRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            CheckComponent(current);
            foreach (
                string component in fullPath
                    .Substring(assetsRoot.Length)
                    .Split(Path.DirectorySeparatorChar)
            )
            {
                if (component.Length != 0)
                {
                    current = Path.Combine(current, component);
                    CheckComponent(current);
                }
            }

            static void CheckComponent(string path)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        $"The project path '{path}' uses a linked file or directory."
                    );
                }
            }
        }

        private static string FindAssemblySettingsMismatch(
            string assetPath,
            Dictionary<string, byte[]> files
        )
        {
            byte[] settings = files[assetPath];
            TextAsset imported =
                settings == null ? null : AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
            bool hasImportedSettings = imported != null;
            if (
                settings == null
                || !hasImportedSettings
                || !imported.bytes.AsSpan().SequenceEqual(settings)
            )
            {
                return $"{assetPath}: Unity's imported assembly settings could not be matched to disk.";
            }

            // A GUID reference can change its target without changing the assembly settings text.
            string metadataPath = assetPath + ".meta";
            if (
                !files.TryGetValue(metadataPath, out byte[] metadata)
                || metadata == null
                || !HasMatchingAssemblyGuid(metadata, AssetDatabase.AssetPathToGUID(assetPath))
            )
            {
                return $"{metadataPath}: the on-disk GUID could not be matched to Unity's imported assembly asset.";
            }
            return null;
        }

        private static bool HasMatchingAssemblyGuid(byte[] metadata, string importedGuid)
        {
            if (!Guid.TryParseExact(importedGuid, "N", out Guid expected))
            {
                return false;
            }

            try
            {
                using StringReader reader = new(EncodedSource.FromBytes(metadata).Text);
                bool matched = false;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    const string GuidPrefix = "guid:";
                    if (!line.StartsWith(GuidPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (
                        matched
                        || !Guid.TryParseExact(
                            line.Substring(GuidPrefix.Length).Trim(),
                            "N",
                            out Guid actual
                        )
                        || actual != expected
                    )
                    {
                        return false;
                    }
                    matched = true;
                }
                return matched;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static void VerifyProjectFileSnapshot(
            string assetsRoot,
            Dictionary<string, byte[]> originalFiles,
            Dictionary<string, string> originalAssemblies,
            bool assemblySettingsWereCurrent,
            IReadOnlyDictionary<string, byte[]> expectedWrites = null
        )
        {
            HashSet<string> importedScripts = GetImportedScriptPaths();
            string[] currentPaths = GetProjectFilePaths(assetsRoot, importedScripts);
            if (
                currentPaths.Length != originalFiles.Count
                || currentPaths.Any(path => !originalFiles.ContainsKey(path))
            )
            {
                throw new IOException(
                    "The project's C# script or assembly settings inventory changed after the preview."
                );
            }

            if (
                !importedScripts.SetEquals(originalAssemblies.Keys)
                || importedScripts.Any(path =>
                    originalAssemblies[path]
                    != CompilationPipeline.GetAssemblyNameFromScriptPath(path)
                )
            )
            {
                throw new IOException(
                    "The project's script assembly assignments changed after the preview."
                );
            }

            foreach (string path in currentPaths)
            {
                byte[] original = originalFiles[path];
                if (original != null)
                {
                    string fullPath = GetProjectFileFullPath(assetsRoot, path);
                    byte[] expected =
                        expectedWrites != null
                        && expectedWrites.TryGetValue(fullPath, out byte[] upgraded)
                            ? upgraded
                            : original;
                    if (!File.ReadAllBytes(fullPath).AsSpan().SequenceEqual(expected))
                    {
                        throw new IOException(
                            $"The project file '{path}' changed after the preview."
                        );
                    }
                }
            }

            if (assemblySettingsWereCurrent)
            {
                foreach (string path in currentPaths.Where(IsAssemblySettingsPath))
                {
                    string mismatch = FindAssemblySettingsMismatch(path, originalFiles);
                    if (mismatch != null)
                    {
                        throw new IOException(
                            $"Unity's imported assembly settings changed after the preview. {mismatch}"
                        );
                    }
                }
            }
        }

        private sealed class ProjectPackageBindings
        {
            private static readonly string[] PackageTypeNames =
            {
                "MessageAwareComponent",
                "MessageRegistrationToken",
                "MessageHandler",
                "InstanceId",
                "StringMessage",
                "GlobalStringMessage",
                "IUntargetedMessage",
                "ITargetedMessage",
                "IBroadcastMessage",
            };
            private static readonly byte[][] PackageTypeBytes = PackageTypeNames
                .Select(Encoding.UTF8.GetBytes)
                .ToArray();
            private static readonly Regex NonBindingCompilerOption = new(
                @"\A[-/](?:(?:d|define|langversion|nullable|nowarn|warn|warnaserror):[\w.,;_+-]+|(?:unsafe|checked|optimize|warnaserror)[+-]?|additionalfile:(?:""[^""\r\n]+""|[^\s""]+))\z",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            );

            private static readonly Regex AssemblyReferenceSetting = new(
                @"\A\s*\{\s*""reference""\s*:\s*""(?<target>[^""\\]+)""\s*\}\s*\z",
                RegexOptions.CultureInvariant
            );

            private readonly string _projectRoot;
            private readonly Dictionary<string, string> _scriptAssemblies;
            private readonly Dictionary<string, byte[]> _originalFiles;
            private readonly HashSet<(string Path, string Type)> _provedBindings = new();
            private readonly Dictionary<(string Assembly, string Type), string> _assemblyIssues =
                new();
            private readonly Dictionary<string, HashSet<string>> _sourceNames = new(
                StringComparer.Ordinal
            );
            private readonly Dictionary<string, HashSet<string>> _metadataNames = new(
                StringComparer.Ordinal
            );
            private Assembly[] _editorAssemblies;
            private Assembly[] _playerAssemblies;
            private string _runtimePath;
            private string _runtimeRoot;
            private string _providerIssue;
            private Dictionary<string, (Guid Algorithm, byte[] Checksum)> _runtimeDocuments;

            private static readonly Guid Sha256DocumentChecksum = new(
                "8829d00f-11b8-4213-878b-770e8597ac16"
            );
            private static readonly Guid Sha1DocumentChecksum = new(
                "ff1816ec-aa5e-4d10-87f7-6f4963833460"
            );
            private readonly Dictionary<Assembly, string> _providerInputsIssues = new();
            private Dictionary<string, string> _outsideAssemblyReferences;

            public ProjectPackageBindings(
                string assetsRoot,
                Dictionary<string, string> scriptAssemblies,
                Dictionary<string, byte[]> originalFiles
            )
            {
                _projectRoot = Path.GetDirectoryName(
                    assetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                );
                _scriptAssemblies = scriptAssemblies;
                _originalFiles = originalFiles;
            }

            public string FindIssue(string assetPath, string type)
            {
                if (Array.IndexOf(PackageTypeNames, type) < 0)
                {
                    return "the preview has no compilation proof for this package type";
                }
                if (
                    !_scriptAssemblies.TryGetValue(assetPath, out string assemblyName)
                    || string.IsNullOrEmpty(assemblyName)
                )
                {
                    return "Unity has not established this script's compilation assembly";
                }
                if (assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    assemblyName = assemblyName.Substring(0, assemblyName.Length - 4);
                }
                try
                {
                    _editorAssemblies ??= CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                    _playerAssemblies ??= CompilationPipeline.GetAssemblies(AssembliesType.Player);
                    Assembly editor = _editorAssemblies.FirstOrDefault(assembly =>
                        assembly.name == assemblyName
                    );
                    if (editor == null || !ContainsScript(editor, assetPath))
                    {
                        return "Unity's Editor compilation does not list this script in its assigned assembly";
                    }
                    Assembly player = _playerAssemblies.FirstOrDefault(assembly =>
                        assembly.name == assemblyName
                    );
                    if (player != null && !ContainsScript(player, assetPath))
                    {
                        return "Unity's Player compilation assigns this script differently";
                    }
                    var key = (assemblyName, type);
                    if (!_assemblyIssues.TryGetValue(key, out string issue))
                    {
                        issue = FindAssemblyIssue(editor, type);
                        if (issue == null && player != null)
                        {
                            issue = FindAssemblyIssue(player, type);
                        }
                        if (
                            issue == null
                            && player == null
                            && (editor.flags & AssemblyFlags.EditorAssembly) == 0
                        )
                        {
                            issue =
                                "Unity has not established the Player compilation for this assembly";
                        }
                        _assemblyIssues.Add(key, issue);
                    }
                    if (issue == null)
                    {
                        _provedBindings.Add((assetPath, type));
                    }
                    return issue;
                }
                catch (Exception exception)
                    when (exception
                            is IOException
                                or UnauthorizedAccessException
                                or ArgumentException
                                or InvalidOperationException
                                or NotSupportedException
                    )
                {
                    return $"the compilation inputs could not be verified ({exception.Message})";
                }
            }

            public void Verify()
            {
                if (_provedBindings.Count == 0)
                {
                    return;
                }

                ProjectPackageBindings current = new(
                    Path.Combine(_projectRoot, "Assets"),
                    _scriptAssemblies,
                    _originalFiles
                );
                foreach ((string path, string type) in _provedBindings)
                {
                    string issue = current.FindIssue(path, type);
                    if (issue != null)
                    {
                        throw new IOException(
                            $"Could not recheck the package {type} binding for '{path}': {issue}."
                        );
                    }
                }
            }

            private bool ContainsScript(Assembly assembly, string assetPath)
            {
                string fullPath = PhysicalPath(assetPath);
                return assembly.sourceFiles.Any(path => PhysicalPath(path) == fullPath);
            }

            private string FindAssemblyIssue(Assembly assembly, string type)
            {
                foreach (string input in assembly.sourceFiles)
                {
                    string path = PhysicalPath(input);
                    string relative = Path.GetRelativePath(_projectRoot, path)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (
                        !_scriptAssemblies.ContainsKey(relative)
                        || !_originalFiles.TryGetValue(relative, out byte[] bytes)
                        || bytes == null
                    )
                    {
                        return $"the compilation includes a source outside the script index ('{input}')";
                    }
                }

                string definition =
                    CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(
                        assembly.name
                    );
                if (!string.IsNullOrEmpty(definition))
                {
                    _outsideAssemblyReferences ??= ReadOutsideAssemblyReferences();
                    if (
                        _outsideAssemblyReferences.TryGetValue(
                            PhysicalPath(definition),
                            out string referencePath
                        )
                    )
                    {
                        return $"an assembly reference outside Assets ('{referencePath}') can add source outside the script index";
                    }
                }

                string issue = FindProviderIssue(assembly, out Assembly provider);
                if (issue != null)
                {
                    return issue;
                }
                issue = FindCompilerOptionsIssue(assembly);
                if (issue != null)
                {
                    return issue;
                }

                foreach (string reference in assembly.compiledAssemblyReferences)
                {
                    string path = PhysicalPath(reference);
                    if (
                        path != _runtimePath
                        && !IsEditorInstallationFile(path)
                        && ReadMetadataNames(path).Contains(type)
                    )
                    {
                        return $"metadata from '{reference}' can affect the package type binding";
                    }
                }

                foreach (Assembly reference in assembly.assemblyReferences)
                {
                    if (reference == provider)
                    {
                        continue;
                    }
                    issue = FindCompilerOptionsIssue(reference);
                    if (issue != null)
                    {
                        return issue;
                    }
                    foreach (string input in reference.sourceFiles)
                    {
                        if (ReadSourceNames(PhysicalPath(input)).Contains(type))
                        {
                            return $"source in '{input}' can affect the package type binding";
                        }
                    }
                    string path = PhysicalPath(reference.outputPath);
                    if (File.Exists(path) && ReadMetadataNames(path).Contains(type))
                    {
                        return $"metadata from '{reference.outputPath}' can affect the package type binding";
                    }
                }
                return null;
            }

            private Dictionary<string, string> ReadOutsideAssemblyReferences()
            {
                Dictionary<string, string> targets = new(StringComparer.Ordinal);
                foreach (
                    string path in GetImportedPaths("t:AssemblyDefinitionReferenceAsset", null)
                )
                {
                    if (!path.StartsWith("Packages/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    byte[] bytes = File.ReadAllBytes(PhysicalPath(path));
                    TextAsset imported = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                    Match settings = AssemblyReferenceSetting.Match(
                        EncodedSource.FromBytes(bytes).Text
                    );
                    if (
                        imported == null
                        || !bytes.AsSpan().SequenceEqual(imported.bytes)
                        || !settings.Success
                    )
                    {
                        throw new IOException(
                            $"Unity's imported assembly reference '{path}' could not be verified"
                        );
                    }

                    string definition =
                        CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyReference(
                            settings.Groups["target"].Value
                        );
                    if (string.IsNullOrEmpty(definition))
                    {
                        throw new IOException(
                            $"Unity did not resolve the target of assembly reference '{path}'"
                        );
                    }
                    targets.TryAdd(PhysicalPath(definition), path);
                }
                return targets;
            }

            private static bool IsEditorInstallationFile(string path) =>
                IsWithinDirectory(
                    Path.GetFullPath(EditorApplication.applicationContentsPath),
                    path
                );

            private string FindProviderIssue(Assembly consumer, out Assembly provider)
            {
                Type component = typeof(global::DxMessaging.Unity.MessageAwareComponent);
                _runtimePath ??= PhysicalPath(component.Assembly.Location);
                provider = consumer.assemblyReferences.FirstOrDefault(reference =>
                    PhysicalPath(reference.outputPath) == _runtimePath
                );
                if (provider == null)
                {
                    return "the compiler does not directly reference the installed DxMessaging runtime assembly";
                }

                if (_runtimeRoot == null)
                {
                    string definition =
                        CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(
                            component.Assembly.GetName().Name
                        );
                    if (string.IsNullOrEmpty(definition))
                    {
                        return "Unity did not identify the installed DxMessaging runtime assembly definition";
                    }
                    string runtimeRoot = Path.GetDirectoryName(
                        Path.GetDirectoryName(PhysicalPath(definition))
                    );
                    _providerIssue = FindRuntimeApiIssue(component, runtimeRoot);
                    _runtimeRoot = runtimeRoot;
                }

                string baseSource = Path.Combine(_runtimeRoot, "Unity", "MessageAwareComponent.cs");
                if (!provider.sourceFiles.Any(path => PhysicalPath(path) == baseSource))
                {
                    return "the referenced runtime output does not list the installed DxMessaging runtime source";
                }

                if (!_providerInputsIssues.TryGetValue(provider, out string issue))
                {
                    issue = _providerIssue
                        ?? FindCompilerOptionsIssue(provider)
                        ?? FindRuntimeSourceIssue(provider);
                    _providerInputsIssues.Add(provider, issue);
                }
                return issue;
            }

            private string FindRuntimeSourceIssue(Assembly provider)
            {
                using SHA256 sha256 = SHA256.Create();
                // Older Unity compilers can use SHA-1 for Portable PDB document checksums.
                using SHA1 sha1 = SHA1.Create();
                HashSet<string> inputs = new(StringComparer.Ordinal);
                foreach (string input in provider.sourceFiles)
                {
                    string path = PhysicalPath(input);
                    if (!IsWithinDirectory(_runtimeRoot, path))
                    {
                        return $"runtime source '{input}' is outside the installed package";
                    }
                    if (!_runtimeDocuments.TryGetValue(path, out var document))
                    {
                        return $"runtime source '{input}' is missing from the compiled package symbols";
                    }
                    HashAlgorithm hash =
                        document.Algorithm == Sha256DocumentChecksum ? sha256
                        : document.Algorithm == Sha1DocumentChecksum ? sha1
                        : null;
                    if (
                        hash == null
                        || !hash.ComputeHash(File.ReadAllBytes(path))
                            .AsSpan()
                            .SequenceEqual(document.Checksum)
                    )
                    {
                        return $"runtime source '{input}' differs from the compiled package snapshot";
                    }
                    inputs.Add(path);
                }
                foreach (string path in _runtimeDocuments.Keys)
                {
                    if (IsWithinDirectory(_runtimeRoot, path) && !inputs.Contains(path))
                    {
                        return $"the compiled runtime includes '{path}' outside Unity's current source list";
                    }
                }
                return null;
            }

            private static bool IsWithinDirectory(string directory, string path)
            {
                string relative = Path.GetRelativePath(directory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return !Path.IsPathRooted(relative)
                    && relative != ".."
                    && !relative.StartsWith("../", StringComparison.Ordinal);
            }

            private string FindRuntimeApiIssue(Type component, string runtimeRoot)
            {
                byte[] moduleId = component.Module.ModuleVersionId.ToByteArray();
                byte[] runtime = File.ReadAllBytes(_runtimePath);
                if (runtime.AsSpan().IndexOf(moduleId) < 0)
                {
                    return "the runtime reference on disk does not match the loaded DxMessaging module";
                }

                string symbolsPath = Path.ChangeExtension(_runtimePath, ".pdb");
                if (!File.Exists(symbolsPath))
                {
                    return "the installed package runtime has no compiled source checksum evidence";
                }

                Dictionary<string, (Guid Algorithm, byte[] Checksum)> documents =
                    ReadPortableDocuments(File.ReadAllBytes(symbolsPath), runtime);
                if (documents == null)
                {
                    return "the installed package runtime and its source checksum records do not match";
                }

                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic;
                System.Reflection.MethodInfo[] methods = component.GetMethods(flags);
                string source = File.ReadAllText(
                    Path.Combine(runtimeRoot, "Unity", "MessageAwareComponent.cs")
                );
                SourceMetadata metadata = new(source);
                string masked = MaskNonCode(source, null, metadata);
                if (
                    metadata.UncertainDirectivePosition != null
                    || ContainsNamespaceAlias(masked, "StringMessage")
                    || ContainsNamespaceAlias(masked, "GlobalStringMessage")
                    || ContainsNamespaceAlias(masked, "Core")
                    || ContainsNamespaceAlias(masked, "DxMessaging")
                )
                {
                    return "the runtime base source has unverified payload bindings";
                }
                foreach (string name in ChangedOverrideNames)
                {
                    string payload =
                        name == "HandleGlobalStringMessage"
                            ? "GlobalStringMessage"
                            : "StringMessage";
                    System.Reflection.MethodInfo[] candidates = methods
                        .Where(method => method.Name == name)
                        .ToArray();
                    List<ParameterList> declarations = FindMethodDeclarations(masked, name, false);
                    if (candidates.Length != 1 || declarations.Count != 1)
                    {
                        return $"the runtime base has an unverified {name} overload";
                    }

                    System.Reflection.ParameterInfo[] parameters = candidates[0].GetParameters();
                    ParameterList declaration = declarations[0];
                    string sourceParameter = masked.Substring(
                        declaration.ParametersStart,
                        declaration.ParametersEnd - declaration.ParametersStart
                    );
                    if (
                        candidates[0].DeclaringType != component
                        || !candidates[0].IsVirtual
                        || parameters.Length != 1
                        || !parameters[0].IsIn
                        || parameters[0].ParameterType.GetElementType()?.FullName
                            != "DxMessaging.Core.Messages." + payload
                        || !Regex.IsMatch(
                            sourceParameter,
                            @"\A\s*in\s+" + payload + @"\s+" + IdentifierSpellingPattern + @"\s*\z",
                            RegexOptions.CultureInvariant
                        )
                        || metadata.UncertainBranches.Exists(branch =>
                            branch.Start <= declaration.DeclarationStart
                            && declaration.DeclarationStart < branch.Start + branch.Length
                            && FindContainingTypeStart(masked, declaration.DeclarationStart)
                                < branch.Start
                        )
                    )
                    {
                        return $"the installed {name} signature could not be verified";
                    }
                }
                _runtimeDocuments = new(StringComparer.Ordinal);
                foreach (var (name, document) in documents)
                {
                    string path = name.Replace('\\', '/');
                    if (path.StartsWith("./", StringComparison.Ordinal))
                    {
                        path = path.Substring(2);
                    }
                    if (
                        Path.IsPathRooted(path)
                        || path.StartsWith("Assets/", StringComparison.Ordinal)
                        || path.StartsWith("Packages/", StringComparison.Ordinal)
                    )
                    {
                        _runtimeDocuments.Add(PhysicalPath(path), document);
                    }
                }
                return null;
            }

            private static Dictionary<
                string,
                (Guid Algorithm, byte[] Checksum)
            > ReadPortableDocuments(byte[] symbols, byte[] runtime)
            {
                /*
                 * A portable PDB stores its content identifier in #Pdb, document rows in
                 * #~, and their names and checksums in #Blob. Link that identifier to the
                 * runtime PE's CodeView record before trusting the document rows.
                 */
                using BinaryReader reader = new(new MemoryStream(symbols, writable: false));
                if (reader.ReadUInt32() != 0x424A5342)
                {
                    return null;
                }
                reader.BaseStream.Position = 12;
                uint versionLength = reader.ReadUInt32();
                reader.BaseStream.Position += versionLength;
                reader.ReadUInt16();
                int streamCount = reader.ReadUInt16();
                Dictionary<string, TextSpan> streams = new(StringComparer.Ordinal);
                byte[] name = new byte[32];
                for (int index = 0; index < streamCount; index++)
                {
                    uint offset = reader.ReadUInt32();
                    uint size = reader.ReadUInt32();
                    int length = 0;
                    while (length < name.Length && (name[length] = reader.ReadByte()) != 0)
                    {
                        length++;
                    }
                    if (length == name.Length || symbols.LongLength < (long)offset + size)
                    {
                        return null;
                    }
                    reader.BaseStream.Position = (reader.BaseStream.Position + 3) & ~3L;
                    streams.Add(
                        Encoding.ASCII.GetString(name, 0, length),
                        new TextSpan((int)offset, (int)size)
                    );
                }
                if (
                    !streams.TryGetValue("#Pdb", out TextSpan pdb)
                    || pdb.Length < 20
                    || !streams.TryGetValue("#~", out TextSpan tables)
                    || tables.Length < 24
                    || !streams.TryGetValue("#Blob", out TextSpan blobs)
                    || !streams.TryGetValue("#GUID", out TextSpan guids)
                )
                {
                    return null;
                }
                byte[] codeView = new byte[20];
                // cspell:ignore RSDS
                Encoding.ASCII.GetBytes("RSDS", 0, 4, codeView, 0);
                Buffer.BlockCopy(symbols, pdb.Start, codeView, 4, 16);
                if (runtime.AsSpan().IndexOf(codeView) < 0)
                {
                    return null;
                }

                reader.BaseStream.Position = tables.Start + 6;
                byte heaps = reader.ReadByte();
                reader.ReadByte();
                ulong valid = reader.ReadUInt64();
                reader.ReadUInt64();
                const ulong DocumentTable = 1UL << 48;
                if ((valid & DocumentTable) == 0 || (valid & (DocumentTable - 1)) != 0)
                {
                    return null;
                }
                int count = reader.ReadInt32();
                for (ulong other = valid >> 49; other != 0; other >>= 1)
                {
                    if ((other & 1) != 0)
                    {
                        reader.ReadUInt32();
                    }
                }
                bool wideGuid = (heaps & 2) != 0;
                bool wideBlob = (heaps & 4) != 0;
                int rowSize = (wideGuid ? 8 : 4) + (wideBlob ? 8 : 4);
                if (
                    count < 0
                    || tables.Start + tables.Length
                        < reader.BaseStream.Position + (long)count * rowSize
                )
                {
                    return null;
                }

                Dictionary<string, (Guid Algorithm, byte[] Checksum)> documents = new(
                    StringComparer.Ordinal
                );
                ReadOnlySpan<byte> blobHeap = symbols.AsSpan(blobs.Start, blobs.Length);
                ReadOnlySpan<byte> guidHeap = symbols.AsSpan(guids.Start, guids.Length);
                for (int index = 0; index < count; index++)
                {
                    int documentName = ReadSymbolHeapIndex(reader, wideBlob);
                    int algorithm = ReadSymbolHeapIndex(reader, wideGuid);
                    int documentHash = ReadSymbolHeapIndex(reader, wideBlob);
                    ReadSymbolHeapIndex(reader, wideGuid);
                    if (algorithm == 0 && documentHash == 0)
                    {
                        continue;
                    }
                    if (
                        algorithm <= 0
                        || guidHeap.Length / 16 < algorithm
                        || documentName <= 0
                        || documentHash <= 0
                    )
                    {
                        return null;
                    }
                    string path = DecodeDocumentName(blobHeap, documentName);
                    Guid algorithmId = new(guidHeap.Slice((algorithm - 1) * 16, 16));
                    if (
                        !documents.TryAdd(
                            path,
                            (algorithmId, ReadSymbolBlob(blobHeap, documentHash).ToArray())
                        )
                    )
                    {
                        return null;
                    }
                }
                return documents;
            }

            private static int ReadSymbolHeapIndex(BinaryReader reader, bool wide) =>
                wide ? reader.ReadInt32() : reader.ReadUInt16();

            private static int ReadCompressedSymbolIndex(ReadOnlySpan<byte> data, ref int offset)
            {
                int first = data.Slice(offset++, 1)[0];
                if ((first & 0x80) == 0)
                {
                    return first;
                }
                int second = data.Slice(offset++, 1)[0];
                if ((first & 0xC0) == 0x80)
                {
                    return ((first & 0x3F) << 8) | second;
                }
                if ((first & 0xE0) == 0xC0)
                {
                    ReadOnlySpan<byte> tail = data.Slice(offset, 2);
                    offset += 2;
                    return ((first & 0x1F) << 24) | (second << 16) | (tail[0] << 8) | tail[1];
                }
                throw new IOException("Invalid compressed index in the runtime symbols.");
            }

            private static ReadOnlySpan<byte> ReadSymbolBlob(ReadOnlySpan<byte> heap, int index)
            {
                int length = ReadCompressedSymbolIndex(heap, ref index);
                return heap.Slice(index, length);
            }

            private static string DecodeDocumentName(ReadOnlySpan<byte> blobHeap, int index)
            {
                ReadOnlySpan<byte> name = ReadSymbolBlob(blobHeap, index);
                string separator = name.Slice(0, 1)[0] == 0 ? "" : ((char)name[0]).ToString();
                List<string> parts = new();
                int offset = 1;
                while (offset < name.Length)
                {
                    int part = ReadCompressedSymbolIndex(name, ref offset);
                    parts.Add(Encoding.UTF8.GetString(ReadSymbolBlob(blobHeap, part)));
                }
                return string.Join(separator, parts);
            }

            private string FindCompilerOptionsIssue(Assembly assembly)
            {
                ScriptCompilerOptions options = assembly.compilerOptions;
                if (
                    options == null
                    || (options.AdditionalCompilerArguments ?? Array.Empty<string>()).Any(
                        argument => !NonBindingCompilerOption.IsMatch(argument.Trim())
                    )
                )
                {
                    return $"compiler options for '{assembly.name}' can affect the package type binding";
                }

                foreach (string file in options.ResponseFiles ?? Array.Empty<string>())
                {
                    if (
                        File.ReadLines(PhysicalPath(file))
                            .Any(line =>
                            {
                                string option = line.Trim();
                                return option.Length != 0
                                    && !option.StartsWith("#", StringComparison.Ordinal)
                                    && !NonBindingCompilerOption.IsMatch(option);
                            })
                    )
                    {
                        return $"compiler response file '{file}' can affect the package type binding";
                    }
                }

                foreach (string analyzer in options.RoslynAnalyzerDllPaths ?? Array.Empty<string>())
                {
                    string path = PhysicalPath(analyzer);
                    string analyzers = Path.Combine(_runtimeRoot, "Analyzers");
                    if (IsEditorInstallationFile(path))
                    {
                        continue;
                    }
                    if (
                        path != Path.Combine(analyzers, "WallstopStudios.DxMessaging.Analyzer.dll")
                        && path
                            != Path.Combine(
                                analyzers,
                                "WallstopStudios.DxMessaging.SourceGenerators.dll"
                            )
                    )
                    {
                        return $"compiler analyzer '{analyzer}' could supply a competing type";
                    }
                }
                return null;
            }

            private HashSet<string> ReadSourceNames(string path)
            {
                if (!_sourceNames.TryGetValue(path, out HashSet<string> names))
                {
                    string source = EncodedSource.FromBytes(File.ReadAllBytes(path)).Text;
                    SourceMetadata metadata = new(source);
                    string masked = MaskNonCode(source, null, metadata);
                    if (metadata.UncertainDirectivePosition == null)
                    {
                        source = masked;
                    }
                    names = PackageTypeNames
                        .Where(name => source.Contains(name, StringComparison.Ordinal))
                        .ToHashSet(StringComparer.Ordinal);
                    if (
                        source.Contains('\\')
                        || source.Any(character =>
                            CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format
                        )
                    )
                    {
                        foreach (Match token in IdentifierUseRegex.Matches(source))
                        {
                            string name = IdentifierValue(token.Value);
                            if (Array.IndexOf(PackageTypeNames, name) >= 0)
                            {
                                names.Add(name);
                            }
                        }
                    }
                    _sourceNames.Add(path, names);
                }
                return names;
            }

            private HashSet<string> ReadMetadataNames(string path)
            {
                if (!_metadataNames.TryGetValue(path, out HashSet<string> names))
                {
                    /*
                     * CLI metadata stores type names as UTF-8 strings. Absence rules out
                     * declarations and forwarded names. Presence is inconclusive.
                     */
                    byte[] bytes = File.ReadAllBytes(path);
                    names = new HashSet<string>(StringComparer.Ordinal);
                    for (int index = 0; index < PackageTypeNames.Length; index++)
                    {
                        if (0 <= bytes.AsSpan().IndexOf(PackageTypeBytes[index]))
                        {
                            names.Add(PackageTypeNames[index]);
                        }
                    }
                    _metadataNames.Add(path, names);
                }
                return names;
            }

            private string PhysicalPath(string path) =>
                Path.GetFullPath(
                    Path.IsPathRooted(path) ? path : FileUtil.GetPhysicalPath(path),
                    _projectRoot
                );
        }

        internal static UpgradeResult Analyze(
            string source,
            Func<string, string> findExternalCallbackUse = null,
            ProjectCallbackUses projectTypes = null,
            string assemblyName = null,
            bool projectSourcesLimitedToAssets = false,
            Func<string, string> findPackageBindingIssue = null
        )
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            SourceMetadata metadata = new(
                source,
                findExternalCallbackUse,
                projectTypes,
                assemblyName,
                projectSourcesLimitedToAssets,
                findPackageBindingIssue
            );
            string masked = MaskNonCode(source, metadata.InterpolatedExpressions, metadata);
            if (metadata.UncertainDirectivePosition is int directive)
            {
                return new UpgradeResult(
                    source,
                    0,
                    0,
                    new List<string> { ConditionalDirectiveIssue(source, directive) }
                );
            }

            metadata.InterpolatedExpressions.Sort(
                static (left, right) => left.Start.CompareTo(right.Start)
            );
            HashSet<int> replacementStarts = new();
            SortedSet<string> manualReview = new(StringComparer.Ordinal);
            Dictionary<string, string> unprovenMethodGroups = new(StringComparer.Ordinal);
            List<Invocation> readonlyInvocations = FindInvocations(
                masked,
                ReadonlyRegistrationNames,
                manualReview,
                metadata,
                requireProvenReceiver: true,
                unprovenMethodGroups: unprovenMethodGroups
            );
            List<Invocation> mutableInvocations = FindInvocations(
                masked,
                MutableRegistrationNames,
                manualReview: null,
                metadata,
                requireProvenReceiver: false
            );
            HashSet<string> mutableMethodGroups = CollectCallbackMethodGroups(
                masked,
                mutableInvocations
            );
            List<(string Name, TextSpan Use)> methodGroups = new();

            foreach (Invocation invocation in readonlyInvocations)
            {
                foreach (TextSpan callback in GetCallbackArguments(masked, invocation))
                {
                    AddCallbackReplacements(
                        masked,
                        metadata,
                        callback,
                        replacementStarts,
                        methodGroups,
                        manualReview
                    );
                }
            }

            AddFastDelegateReplacements(
                masked,
                metadata,
                replacementStarts,
                manualReview,
                methodGroups,
                unprovenMethodGroups
            );
            foreach (
                var group in methodGroups.GroupBy(use =>
                    ScopedMethodKey(masked, use.Name, use.Use.Start)
                )
            )
            {
                (string name, TextSpan use) = group.First();
                if (mutableMethodGroups.Contains(group.Key))
                {
                    manualReview.Add(
                        $"{CallbackLocation(masked, use.Start)} {name} is also used as an interceptor; split the callbacks before changing it"
                    );
                    continue;
                }
                if (unprovenMethodGroups.TryGetValue(group.Key, out string reason))
                {
                    manualReview.Add(reason);
                    continue;
                }
                AddMethodDeclarationReplacements(
                    masked,
                    metadata,
                    name,
                    use.Start,
                    group.Select(entry => entry.Use),
                    replacementStarts,
                    manualReview
                );
            }

            Dictionary<int, int> baseForwardingOwners = new();
            foreach (string overrideName in ChangedOverrideNames)
            {
                AddOverrideDeclarationReplacements(
                    source,
                    masked,
                    metadata,
                    overrideName,
                    replacementStarts,
                    baseForwardingOwners,
                    manualReview
                );
            }

            PruneIncompatibleParameterUses(
                source,
                masked,
                metadata,
                readonlyInvocations,
                replacementStarts,
                baseForwardingOwners,
                manualReview
            );

            SortedDictionary<int, string> insertions = new();
            AddStringHandlerOptIns(masked, metadata, replacementStarts, insertions, manualReview);
            manualReview.UnionWith(metadata.PackageBindingReviews);

            List<int> orderedStarts = new(replacementStarts);
            orderedStarts.Sort();
            StringBuilder upgraded = new(source.Length + insertions.Count * 96);
            int previous = 0;
            int replacementIndex = 0;
            foreach (KeyValuePair<int, string> insertion in insertions)
            {
                while (
                    replacementIndex < orderedStarts.Count
                    && orderedStarts[replacementIndex] < insertion.Key
                )
                {
                    int replacementStart = orderedStarts[replacementIndex++];
                    upgraded.Append(source, previous, replacementStart - previous);
                    upgraded.Append("in");
                    previous = replacementStart + 3;
                }
                upgraded.Append(source, previous, insertion.Key - previous);
                upgraded.Append(insertion.Value);
                previous = insertion.Key;
            }
            while (replacementIndex < orderedStarts.Count)
            {
                int replacementStart = orderedStarts[replacementIndex++];
                upgraded.Append(source, previous, replacementStart - previous);
                upgraded.Append("in");
                previous = replacementStart + 3;
            }
            upgraded.Append(source, previous, source.Length - previous);

            return new UpgradeResult(
                upgraded.ToString(),
                orderedStarts.Count,
                insertions.Count,
                new List<string>(manualReview)
            );
        }

        internal static byte[] UpgradeBytes(byte[] sourceBytes, out UpgradeResult result)
        {
            if (sourceBytes == null)
            {
                throw new ArgumentNullException(nameof(sourceBytes));
            }

            EncodedSource source = EncodedSource.FromBytes(sourceBytes);
            result = Analyze(source.Text);
            return source.Encode(result.UpgradedSource);
        }

        internal static bool IsGeneratedSource(string assetPath, string source)
        {
            if (assetPath == null)
            {
                throw new ArgumentNullException(nameof(assetPath));
            }
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (IsGeneratedPath(assetPath))
            {
                return true;
            }

            int headerLength = Math.Min(source.Length, 2048);
            string header = source.Substring(0, headerLength);
            int position = 0;
            while (position < header.Length)
            {
                while (position < header.Length && char.IsWhiteSpace(header[position]))
                {
                    position++;
                }
                if (header.Length <= position + 1)
                {
                    return false;
                }

                string comment;
                if (header[position] == '/' && header[position + 1] == '/')
                {
                    int lineEnd = header.IndexOf('\n', position + 2);
                    lineEnd = lineEnd < 0 ? header.Length : lineEnd;
                    comment = header.Substring(position + 2, lineEnd - position - 2);
                    position = lineEnd + 1;
                }
                else if (header[position] == '/' && header[position + 1] == '*')
                {
                    int commentEnd = header.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    if (commentEnd < 0)
                    {
                        return false;
                    }
                    comment = header.Substring(position + 2, commentEnd - position - 2);
                    position = commentEnd + 2;
                }
                else
                {
                    return false;
                }

                comment = comment.TrimStart().TrimStart('*').TrimStart();
                if (
                    Regex.IsMatch(
                        comment,
                        @"^(?:<auto-generated(?:\s*/)?\s*>|@generated\b)",
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
                    )
                )
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsGeneratedPath(string assetPath)
        {
            string normalized = assetPath.Replace('\\', '/');
            return normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
                || 0 <= normalized.IndexOf("/Generated/", StringComparison.OrdinalIgnoreCase)
                || 0 <= normalized.IndexOf("/GeneratedCode/", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddFastDelegateReplacements(
            string masked,
            SourceMetadata metadata,
            HashSet<int> replacementStarts,
            SortedSet<string> manualReview,
            List<(string Name, TextSpan Use)> methodGroups,
            Dictionary<string, string> unprovenMethodGroups
        )
        {
            foreach (Match handler in FastDelegateNames(masked, metadata))
            {
                int fastHandler = handler.Index;
                bool isCandidate = TryClassifyFastDelegateType(
                    masked,
                    fastHandler,
                    metadata,
                    out bool isProven,
                    out bool hasHandlerSyntax
                );
                if (!hasHandlerSyntax)
                {
                    continue;
                }

                foreach (
                    (
                        int variable,
                        TextSpan rightHandSideSpan,
                        string messageType,
                        bool declarationIsProven
                    ) in FindFastDelegateInitializers(masked, handler, metadata)
                )
                {
                    Invocation expression = new(
                        "FastHandler",
                        rightHandSideSpan.Start,
                        rightHandSideSpan.Start + rightHandSideSpan.Length
                    );
                    if (!isProven || !declarationIsProven)
                    {
                        string binding = declarationIsProven
                            ? "MessageHandler type"
                            : "auto-property declaration";
                        string reason =
                            $"{CallbackLocation(masked, variable)} uses a delegate whose {binding} cannot be proven; inspect its binding manually";
                        if (TryGetMethodGroup(masked, rightHandSideSpan, out string name, out _))
                        {
                            unprovenMethodGroups.TryAdd(
                                ScopedMethodKey(masked, name, rightHandSideSpan.Start),
                                reason
                            );
                        }
                        if (!isCandidate)
                        {
                            continue;
                        }
                        bool needsReview = TryGetInlineDelegateParameters(
                            masked,
                            expression,
                            out TextSpan parameters
                        )
                            ? HasRefParameter(
                                masked,
                                parameters.Start,
                                parameters.Start + parameters.Length
                            )
                            : MethodGroupMayNeedMigration(
                                masked,
                                rightHandSideSpan,
                                masked,
                                metadata
                            );
                        if (needsReview)
                        {
                            manualReview.Add(reason);
                        }
                        continue;
                    }

                    metadata.FastDelegateInitializers.Add(
                        (
                            fastHandler,
                            variable,
                            rightHandSideSpan,
                            messageType,
                            IdentifierValue(handler.Value) == "FastHandlerWithContext"
                        )
                    );
                    AddCallbackReplacements(
                        masked,
                        metadata,
                        rightHandSideSpan,
                        replacementStarts,
                        methodGroups,
                        manualReview
                    );
                }
            }
        }

        private static IEnumerable<Match> FastDelegateNames(
            string masked,
            SourceMetadata metadata
        ) =>
            metadata
                .MaskedIdentifierUses(masked, "FastHandler")
                .Concat(metadata.MaskedIdentifierUses(masked, "FastHandlerWithContext"))
                .OrderBy(match => match.Index);

        private static IEnumerable<(
            int Variable,
            TextSpan Initializer,
            string MessageType,
            bool DeclarationIsProven
        )> FindFastDelegateInitializers(string masked, Match handler, SourceMetadata metadata)
        {
            int open = NextNonWhitespace(masked, handler.Index + handler.Length);
            if (IsGenericTypeTestOperand(masked, open))
            {
                yield break;
            }

            int close = FindMatching(masked, open, '<', '>');
            if (close < 0)
            {
                yield break;
            }
            string messageType = CompactType(masked.Substring(open + 1, close - open - 1));
            int variable = NextNonWhitespace(masked, close + 1);
            if (0 <= variable && masked[variable] == '?')
            {
                variable = NextNonWhitespace(masked, variable + 1);
            }
            int statementEnd = variable < 0 ? -1 : FindStatementEnd(masked, variable);
            while (0 <= variable && variable < statementEnd)
            {
                Match name = DirectMemberIdentifierRegex.Match(
                    masked,
                    variable,
                    statementEnd - variable
                );
                if (!name.Success)
                {
                    yield break;
                }
                int following = NextNonWhitespace(masked, variable + name.Length);
                if (following < 0 || statementEnd < following)
                {
                    yield break;
                }

                bool declarationIsProven = true;
                if (masked[following] == '{')
                {
                    int accessors = following;
                    int closeAccessors = FindMatching(masked, accessors, '{', '}');
                    following =
                        closeAccessors < 0 ? -1 : NextNonWhitespace(masked, closeAccessors + 1);
                    if (following < 0 || statementEnd <= following || masked[following] != '=')
                    {
                        yield break;
                    }

                    int declarationStart =
                        masked.LastIndexOfAny(new[] { '{', '}', ';' }, handler.Index - 1) + 1;
                    declarationIsProven =
                        IsAutoPropertyAccessorList(masked, accessors, closeAccessors)
                        && !metadata.UncertainBranches.Exists(branch =>
                            branch.Start <= following
                            && declarationStart < branch.Start + branch.Length
                        );
                }

                if (masked[following] == '=' && masked[following + 1] is not ('=' or '>'))
                {
                    int initializer = following + 1;
                    following = FindExpressionEnd(masked, initializer);
                    if (statementEnd < following)
                    {
                        yield break;
                    }
                    yield return (
                        variable,
                        new TextSpan(initializer, following - initializer),
                        messageType,
                        declarationIsProven
                    );
                }

                if (masked[following] != ',')
                {
                    yield break;
                }
                variable = NextNonWhitespace(masked, following + 1);
                if (variable < 0)
                {
                    yield break;
                }
            }
        }

        private static bool IsAutoPropertyAccessorList(string masked, int open, int close)
        {
            bool hasGetter = false;
            bool hasSetter = false;
            int cursor = NextNonWhitespace(masked, open + 1);
            while (0 <= cursor && cursor < close)
            {
                cursor = SkipDeclarationAttributes(masked, cursor);
                Match token =
                    cursor < 0 ? Match.Empty : DirectMemberIdentifierRegex.Match(masked, cursor);
                while (
                    token.Success
                    && token.Value
                        is "public"
                            or "internal"
                            or "protected"
                            or "private"
                            or "readonly"
                )
                {
                    cursor = NextNonWhitespace(masked, token.Index + token.Length);
                    token =
                        cursor < 0
                            ? Match.Empty
                            : DirectMemberIdentifierRegex.Match(masked, cursor);
                }
                if (!token.Success)
                {
                    return false;
                }

                switch (token.Value)
                {
                    case "get" when !hasGetter:
                        hasGetter = true;
                        break;
                    case "set" or "init" when !hasSetter:
                        hasSetter = true;
                        break;
                    default:
                        return false;
                }

                int semicolon = NextNonWhitespace(masked, token.Index + token.Length);
                if (semicolon < 0 || close <= semicolon || masked[semicolon] != ';')
                {
                    return false;
                }
                cursor = NextNonWhitespace(masked, semicolon + 1);
            }
            return hasGetter;
        }

        private static void AddOverrideDeclarationReplacements(
            string source,
            string masked,
            SourceMetadata metadata,
            string methodName,
            HashSet<int> replacementStarts,
            Dictionary<int, int> baseForwardingOwners,
            SortedSet<string> manualReview
        )
        {
            List<ParameterList> declarations = FindMethodDeclarations(masked, methodName, true);
            foreach (ParameterList declaration in declarations)
            {
                if (
                    !HasRefParameter(masked, declaration.ParametersStart, declaration.ParametersEnd)
                )
                {
                    continue;
                }
                int typeStart = FindContainingTypeStart(masked, declaration.DeclarationStart);
                if (!TypeScopeDerivesFromMessageAwareComponent(masked, typeStart, metadata))
                {
                    replacementStarts.RemoveWhere(start =>
                        declaration.ParametersStart <= start && start < declaration.ParametersEnd
                    );
                    string issue = metadata.PackageBindingIssue(
                        "DxMessaging.Unity.MessageAwareComponent"
                    );
                    string reason =
                        issue == null
                            ? "is not in a directly declared MessageAwareComponent; inspect it manually"
                            : $"needs manual review because {issue}";
                    manualReview.Add(
                        $"{CallbackLocation(masked, declaration.DeclarationStart)} {methodName} override {reason}"
                    );
                    continue;
                }
                string useIssue = FindRelatedOverrideIssue(masked, typeStart, declarations)
                    ?? FindNamedCallbackUseIssue(
                        masked,
                        metadata,
                        methodName,
                        typeStart,
                        declaration,
                        Array.Empty<TextSpan>()
                    );
                if (useIssue != null)
                {
                    metadata.NamedCallbackUseIssues.TryAdd(
                        declaration.ParametersStart - 1,
                        useIssue
                    );
                }
                AddRefReplacements(
                    masked,
                    declaration.ParametersStart,
                    declaration.ParametersEnd,
                    replacementStarts
                );
                AddBaseOverrideForwardingReplacements(
                    source,
                    masked,
                    metadata,
                    declaration,
                    methodName,
                    replacementStarts,
                    baseForwardingOwners
                );
            }
        }

        private static string FindRelatedOverrideIssue(
            string masked,
            int typeStart,
            List<ParameterList> declarations
        )
        {
            if (declarations.Count < 2)
            {
                return null;
            }
            var types = TypeDeclarationRegex
                .Matches(masked)
                .Cast<Match>()
                .Select(match =>
                    (
                        Name: IdentifierValue(match.Groups["name"].Value),
                        HeaderStart: match.Index + match.Length,
                        BodyStart: masked.IndexOf('{', match.Index + match.Length)
                    )
                )
                .Where(type => 0 <= type.BodyStart)
                .ToArray();
            string ownName = types.FirstOrDefault(type => type.BodyStart == typeStart).Name;
            if (ownName == null)
            {
                return null;
            }

            HashSet<string> hierarchy = new(StringComparer.Ordinal) { ownName };
            HashSet<int> descendants = new();
            bool foundType;
            do
            {
                foundType = false;
                foreach (var type in types)
                {
                    if (type.BodyStart == typeStart || descendants.Contains(type.BodyStart))
                    {
                        continue;
                    }
                    string header = masked
                        .Substring(type.HeaderStart, type.BodyStart - type.HeaderStart)
                        .TrimStart();
                    if (
                        header.StartsWith(":", StringComparison.Ordinal)
                        && IdentifierUseRegex
                            .Matches(header)
                            .Cast<Match>()
                            .Any(token => hierarchy.Contains(IdentifierValue(token.Value)))
                    )
                    {
                        descendants.Add(type.BodyStart);
                        foundType |= hierarchy.Add(type.Name);
                    }
                }
            } while (foundType);

            ParameterList related = declarations.FirstOrDefault(candidate =>
                descendants.Contains(FindContainingTypeStart(masked, candidate.DeclarationStart))
                && HasRefParameter(masked, candidate.ParametersStart, candidate.ParametersEnd)
            );
            return related.ParametersEnd == 0
                ? null
                : $"has a related 'ref' override at {SourceLocation(masked, related.DeclarationStart)}";
        }

        private static void AddStringHandlerOptIns(
            string masked,
            SourceMetadata metadata,
            HashSet<int> replacementStarts,
            SortedDictionary<int, string> insertions,
            SortedSet<string> manualReview
        )
        {
            HashSet<int> handledTypeStarts = new();
            foreach (string methodName in ChangedOverrideNames)
            {
                foreach (
                    ParameterList declaration in FindMethodDeclarations(masked, methodName, true)
                )
                {
                    int typeStart = FindContainingTypeStart(masked, declaration.DeclarationStart);
                    if (
                        !TypeScopeDerivesFromMessageAwareComponent(masked, typeStart, metadata)
                        || !HasReplacementInRange(
                            replacementStarts,
                            declaration.ParametersStart,
                            declaration.ParametersEnd
                        )
                        || !handledTypeStarts.Add(typeStart)
                    )
                    {
                        continue;
                    }
                    StringHandlerProperty setting = FindStringHandlerProperty(
                        masked,
                        typeStart,
                        metadata
                    );
                    if (setting != StringHandlerProperty.Missing)
                    {
                        if (setting == StringHandlerProperty.ConditionalOverride)
                        {
                            manualReview.Add(
                                $"{CallbackLocation(masked, declaration.ParametersStart - 1)} has a conditional "
                                    + "RegisterForStringMessages override; check the inherited setting in each build configuration"
                            );
                        }
                        else if (setting == StringHandlerProperty.Collision)
                        {
                            manualReview.Add(
                                $"{CallbackLocation(masked, declaration.ParametersStart - 1)} has a RegisterForStringMessages member that hides the inherited setting; review the opt-in manually"
                            );
                        }
                        else if (setting == StringHandlerProperty.Unproven)
                        {
                            manualReview.Add(
                                $"{CallbackLocation(masked, declaration.DeclarationStart)} has an unverified RegisterForStringMessages declaration. Review the opt-in manually."
                            );
                        }
                        continue;
                    }
                    if (TypeScopeIsPartial(masked, typeStart))
                    {
                        manualReview.Add(
                            $"{CallbackLocation(masked, declaration.DeclarationStart)} {methodName} is declared in a partial MessageAwareComponent; add "
                                + "RegisterForStringMessages => true manually if inherited string handlers are required"
                        );
                        continue;
                    }
                    if (
                        !TryCreateMemberInsertion(
                            masked,
                            typeStart,
                            declaration.DeclarationStart,
                            out string insertion
                        )
                    )
                    {
                        manualReview.Add(
                            $"{CallbackLocation(masked, declaration.DeclarationStart)} {methodName} shares a line with its MessageAwareComponent declaration; add "
                                + "RegisterForStringMessages => true manually if inherited string handlers are required"
                        );
                        continue;
                    }
                    insertions.Add(typeStart + 1, insertion);
                }
            }
        }

        private static bool HasReplacementInRange(
            HashSet<int> replacementStarts,
            int start,
            int end
        )
        {
            return replacementStarts.Any(replacementStart =>
                start <= replacementStart && replacementStart < end
            );
        }

        private enum StringHandlerProperty
        {
            Missing,
            Override,
            ConditionalOverride,
            Collision,
            Unproven,
        }

        private static StringHandlerProperty FindStringHandlerProperty(
            string masked,
            int typeStart,
            SourceMetadata metadata
        )
        {
            int typeEnd = FindMatching(masked, typeStart, '{', '}');
            StringHandlerProperty setting = StringHandlerProperty.Missing;
            for (
                int position = NextNonWhitespace(masked, typeStart + 1);
                0 <= position && position < typeEnd;
                position = FindNextDirectMember(masked, position, typeEnd)
            )
            {
                int start = SkipDeclarationAttributes(masked, position);
                if (start < 0)
                {
                    break;
                }
                Match declaration = DirectMemberDeclarationRegex.Match(masked, start);
                if (!declaration.Success)
                {
                    if (MentionsStringSettingInMemberHeader(masked, start, typeEnd))
                    {
                        return StringHandlerProperty.Unproven;
                    }
                    continue;
                }
                if (
                    !SourceMetadata
                        .ReceiverDeclarators(masked, declaration)
                        .Any(name => IdentifierValue(name.Value) == "RegisterForStringMessages")
                )
                {
                    continue;
                }

                Group name = declaration.Groups["name"];
                int suffix = NextNonWhitespace(masked, name.Index + name.Length);
                bool propertyOverride =
                    IdentifierValue(name.Value) == "RegisterForStringMessages"
                    && 0 <= suffix
                    && (masked[suffix] == '{' || IsArrow(masked, suffix))
                    && declaration
                        .Groups["modifier"]
                        .Captures.Cast<Capture>()
                        .Any(modifier => modifier.Value == "override");
                if (!propertyOverride)
                {
                    return StringHandlerProperty.Collision;
                }
                bool conditional = metadata.UncertainBranches.Exists(branch =>
                    branch.Start <= name.Index && declaration.Index < branch.Start + branch.Length
                );
                if (!conditional || setting == StringHandlerProperty.Missing)
                {
                    setting = conditional
                        ? StringHandlerProperty.ConditionalOverride
                        : StringHandlerProperty.Override;
                }
            }
            return setting;
        }

        private static bool MentionsStringSettingInMemberHeader(string text, int start, int end)
        {
            for (int position = start; position < end; position++)
            {
                char token = text[position];
                if (token is '=' or ';' or '{')
                {
                    break;
                }
                if (token is '(' or '[')
                {
                    position = FindMatching(text, position, token, token == '(' ? ')' : ']');
                    if (position < 0)
                    {
                        break;
                    }
                    continue;
                }
                Match identifier = DirectMemberIdentifierRegex.Match(text, position);
                if (identifier.Success)
                {
                    if (IdentifierValue(identifier.Value) == "RegisterForStringMessages")
                    {
                        return true;
                    }
                    position += identifier.Length - 1;
                }
            }
            return false;
        }

        private static int FindNextDirectMember(string text, int start, int end)
        {
            bool initializer = false;
            for (int index = start; index < end; index++)
            {
                char token = text[index];
                if (token == '=')
                {
                    initializer = true;
                }
                if (token == ';')
                {
                    return NextNonWhitespace(text, index + 1);
                }
                char close = token switch
                {
                    '(' => ')',
                    '[' => ']',
                    '{' => '}',
                    _ => '\0',
                };
                if (close == '\0')
                {
                    continue;
                }
                index = FindMatching(text, index, token, close);
                if (index < 0)
                {
                    return -1;
                }
                if (token == '{' && !initializer)
                {
                    return NextNonWhitespace(text, index + 1);
                }
            }
            return -1;
        }

        private static int SkipDeclarationAttributes(string text, int position)
        {
            while (0 <= position && text[position] == '[')
            {
                int close = FindMatching(text, position, '[', ']');
                position = close < 0 ? -1 : NextNonWhitespace(text, close + 1);
            }
            return position;
        }

        private static bool IsArrow(string text, int position) =>
            position + 1 < text.Length && text[position] == '=' && text[position + 1] == '>';

        private static bool IsDirectTypeMember(string masked, int typeStart, int position)
        {
            for (int index = typeStart + 1; index < position; index++)
            {
                if (masked[index] == '{')
                {
                    int end = FindMatching(masked, index, '{', '}');
                    if (end < 0 || position <= end)
                    {
                        return false;
                    }
                    index = end;
                }
            }
            return true;
        }

        private static bool TypeScopeIsPartial(string masked, int typeStart)
        {
            Match declaration = null;
            foreach (Match match in TypeDeclarationRegex.Matches(masked))
            {
                int open = masked.IndexOf('{', match.Index + match.Length);
                if (open != typeStart)
                {
                    continue;
                }
                declaration = match;
            }
            if (declaration == null)
            {
                return false;
            }

            int modifierStart = declaration.Index - 1;
            while (
                0 <= modifierStart
                && masked[modifierStart] != ';'
                && masked[modifierStart] != '{'
                && masked[modifierStart] != '}'
            )
            {
                modifierStart--;
            }
            return Regex.IsMatch(
                masked.Substring(modifierStart + 1, declaration.Index - modifierStart - 1),
                @"\bpartial\b",
                RegexOptions.CultureInvariant
            );
        }

        private static bool TryCreateMemberInsertion(
            string source,
            int typeStart,
            int memberNameStart,
            out string insertion
        )
        {
            int memberLineStart = source.LastIndexOf('\n', memberNameStart);
            if (memberLineStart <= typeStart)
            {
                insertion = string.Empty;
                return false;
            }
            memberLineStart++;
            int indentationEnd = memberLineStart;
            while (
                indentationEnd < memberNameStart
                && (source[indentationEnd] == ' ' || source[indentationEnd] == '\t')
            )
            {
                indentationEnd++;
            }
            string indentation = source.Substring(
                memberLineStart,
                indentationEnd - memberLineStart
            );
            string lineEnding =
                2 <= memberLineStart && source[memberLineStart - 2] == '\r' ? "\r\n" : "\n";
            insertion =
                lineEnding
                + indentation
                + "protected override bool RegisterForStringMessages => true;"
                + lineEnding;
            return true;
        }

        private static void AddMethodDeclarationReplacements(
            string masked,
            SourceMetadata metadata,
            string methodName,
            int usagePosition,
            IEnumerable<TextSpan> packageUses,
            HashSet<int> replacementStarts,
            SortedSet<string> manualReview
        )
        {
            int usageScope = FindContainingTypeStart(masked, usagePosition);
            List<ParameterList> declarations = FindMethodDeclarations(
                    masked,
                    methodName,
                    requireOverride: false
                )
                .FindAll(declaration =>
                    FindContainingTypeStart(masked, declaration.DeclarationStart) == usageScope
                );
            List<ParameterList> byRefDeclarations = declarations.FindAll(declaration =>
                HasRefParameter(masked, declaration.ParametersStart, declaration.ParametersEnd)
            );
            if (byRefDeclarations.Count == 0)
            {
                if (declarations.Count == 0)
                {
                    manualReview.Add(
                        $"{CallbackLocation(masked, usagePosition)} {methodName} is not declared in this file; inspect its delegate or partial declaration"
                    );
                }
                return;
            }
            if (declarations.Count != 1 || byRefDeclarations.Count != 1)
            {
                manualReview.Add(
                    $"{CallbackLocation(masked, usagePosition)} {methodName} has overloads or multiple declarations"
                );
                return;
            }

            ParameterList declaration = byRefDeclarations[0];
            int nameStart = declaration.DeclarationStart;
            int prefixStart = masked.LastIndexOfAny(new[] { ';', '{', '}' }, nameStart - 1) + 1;
            string prefix = masked.Substring(prefixStart, nameStart - prefixStart);
            string typeHeader = GetTypeHeader(masked, usageScope);
            if (
                Regex.IsMatch(
                    prefix,
                    @"\b(?:abstract|extern|override|partial|virtual)\b",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)
                )
                || (
                    typeHeader.Contains(':')
                    && Regex.IsMatch(
                        prefix,
                        @"\bpublic\b",
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1)
                    )
                )
            )
            {
                manualReview.Add(
                    $"{CallbackLocation(masked, nameStart)} has a declaration that may require its 'ref' signature; inspect its base and interface contracts manually"
                );
                return;
            }
            string useIssue = FindNamedCallbackUseIssue(
                masked,
                metadata,
                methodName,
                usageScope,
                declaration,
                packageUses
            );
            if (useIssue != null)
            {
                metadata.NamedCallbackUseIssues.TryAdd(declaration.ParametersStart - 1, useIssue);
            }
            AddRefReplacements(
                masked,
                declaration.ParametersStart,
                declaration.ParametersEnd,
                replacementStarts
            );
        }

        private static string FindNamedCallbackUseIssue(
            string masked,
            SourceMetadata metadata,
            string methodName,
            int typeStart,
            ParameterList declaration,
            IEnumerable<TextSpan> packageUses
        )
        {
            int[] knownUses = packageUses.Select(use => use.Start).ToArray();
            HashSet<int> declarationNames = FindMethodDeclarations(masked, methodName, false)
                .Select(method => method.DeclarationStart)
                .ToHashSet();
            int declarationBlock = FindEnclosingBrace(masked, declaration.DeclarationStart);
            bool isTypeMember = declarationBlock == typeStart;
            int blockEnd = isTypeMember ? -1 : FindMatching(masked, declarationBlock, '{', '}');
            if (!isTypeMember)
            {
                foreach (int knownUse in knownUses)
                {
                    int before = PreviousNonWhitespace(masked, knownUse - 1);
                    if (
                        knownUse <= declarationBlock
                        || blockEnd <= knownUse
                        || (0 <= before && masked[before] == '.')
                    )
                    {
                        return $"has a callback binding at {SourceLocation(masked, knownUse)} that cannot select the local declaration";
                    }
                }
            }
            foreach (
                (string code, Match use, int offset) in metadata.IdentifierUses(masked, methodName)
            )
            {
                int sourceStart = use.Index + offset;
                if (
                    declarationNames.Contains(sourceStart)
                    || knownUses.Contains(sourceStart)
                    || (
                        !isTypeMember
                        && (sourceStart <= declarationBlock || blockEnd <= sourceStart)
                    )
                )
                {
                    continue;
                }

                int useScope = FindContainingTypeStart(masked, sourceStart);
                int previous = PreviousNonWhitespace(code, use.Index - 1);
                bool qualified = 0 <= previous && code[previous] == '.';
                if (
                    qualified
                        ? !isTypeMember
                            || HasIndependentCallReceiver(
                                masked,
                                metadata,
                                code,
                                previous,
                                sourceStart,
                                typeStart,
                                useScope
                            )
                        : useScope != typeStart
                            && (
                                !isTypeMember
                                || (
                                    (
                                        !GetTypeHeader(masked, useScope).Contains(':')
                                        || TypeScopeDerivesFromMessageAwareComponent(
                                            masked,
                                            useScope,
                                            metadata
                                        )
                                    )
                                    && declarationNames.Any(position =>
                                        FindContainingTypeStart(masked, position) == useScope
                                        && IsDirectTypeMember(masked, useScope, position)
                                    )
                                )
                            )
                )
                {
                    continue;
                }

                int open = NextNonWhitespace(code, use.Index + use.Length);
                if (0 <= open && code[open] == '(')
                {
                    int close = FindMatching(code, open, '(', ')');
                    if (0 <= close)
                    {
                        bool hasRefArgument = SplitArguments(code, open + 1, close)
                            .Exists(argument =>
                                0
                                <= FindBaseArgumentRefStart(
                                    code,
                                    GetArgumentValueSpan(code, argument)
                                )
                            );
                        string location = SourceLocation(masked, sourceStart);
                        return hasRefArgument
                            ? $"has a direct call at {location} that depends on its 'ref' parameter"
                            : $"has a direct call at {location} whose binding can change after migration";
                    }
                }

                bool conditional = metadata.UncertainBranches.Exists(branch =>
                    branch.Start <= sourceStart && sourceStart < branch.Start + branch.Length
                );
                string binding = conditional ? "conditional callback binding" : "callback binding";
                return $"has a {binding} at {SourceLocation(masked, sourceStart)} that cannot be proven safe for 'in'";
            }

            if (
                isTypeMember
                && metadata.FindExternalCallbackUse != null
                && MemberMayHaveExternalUses(
                    masked,
                    typeStart,
                    declaration.DeclarationStart,
                    metadata
                )
            )
            {
                string external = metadata.FindPossibleExternalMemberUse(methodName);
                if (external != null)
                {
                    return $"has a possible callback use in {external} that cannot be proven safe for 'in'";
                }
            }
            return null;
        }

        private static bool MemberMayHaveExternalUses(
            string masked,
            int typeStart,
            int nameStart,
            SourceMetadata metadata
        )
        {
            for (int scope = typeStart; 0 <= scope; scope = FindContainingTypeStart(masked, scope))
            {
                if (TypeScopeIsPartial(masked, scope))
                {
                    return true;
                }
            }

            int start = masked.LastIndexOfAny(new[] { ';', '{', '}' }, nameStart - 1) + 1;
            bool protectedMember = false;
            for (
                Match modifier = IdentifierUseRegex.Match(masked, start, nameStart - start);
                modifier.Success;
                modifier = modifier.NextMatch()
            )
            {
                if (modifier.Value is "public" or "internal")
                {
                    return true;
                }
                protectedMember |= modifier.Value == "protected";
            }
            return protectedMember && !TypeIsUnconditionallySealed(masked, typeStart, metadata);
        }

        private static bool TypeIsUnconditionallySealed(
            string masked,
            int typeStart,
            SourceMetadata metadata
        )
        {
            foreach (Match declaration in TypeDeclarationRegex.Matches(masked))
            {
                if (masked.IndexOf('{', declaration.Index + declaration.Length) != typeStart)
                {
                    continue;
                }

                int position = PreviousNonWhitespace(masked, declaration.Index - 1);
                while (0 <= position)
                {
                    int modifierStart = FindIdentifierStart(masked, position);
                    if (modifierStart < 0)
                    {
                        break;
                    }
                    string modifier = masked.Substring(modifierStart, position - modifierStart + 1);
                    if (modifier == "sealed")
                    {
                        return !metadata.UncertainBranches.Exists(branch =>
                            branch.Start <= modifierStart
                            && modifierStart < branch.Start + branch.Length
                        );
                    }
                    if (
                        modifier
                        is not (
                            "public"
                            or "internal"
                            or "private"
                            or "protected"
                            or "new"
                            or "partial"
                            or "unsafe"
                            or "file"
                        )
                    )
                    {
                        break;
                    }
                    position = PreviousNonWhitespace(masked, modifierStart - 1);
                }
            }
            return false;
        }

        private static bool HasIndependentCallReceiver(
            string masked,
            SourceMetadata metadata,
            string code,
            int dot,
            int usePosition,
            int typeStart,
            int useScope
        )
        {
            int receiverEnd = PreviousNonWhitespace(code, dot - 1);
            if (FollowsKeyword(code, 0, receiverEnd, "base"))
            {
                return typeStart == useScope
                    || TypeScopeDerivesFromMessageAwareComponent(masked, useScope, metadata);
            }
            int receiverStart = FindIdentifierStart(code, receiverEnd);
            if (receiverStart < 0 || !IsRootTypeQualifier(code, receiverStart))
            {
                return false;
            }
            string receiver = IdentifierValue(
                code.Substring(receiverStart, receiverEnd - receiverStart + 1)
            );
            if (receiver == "value" && IsImplicitAccessorValue(masked, usePosition, useScope))
            {
                return false;
            }

            var declarations = metadata
                .ReceiverDeclarations(masked, receiver)
                .Where(entry => FindContainingTypeStart(masked, entry.NameStart) == useScope)
                .ToArray();
            if (
                metadata.HasUnclassifiedReceiverUse(
                    masked,
                    receiver,
                    useScope,
                    declarations.Select(entry => entry.NameStart)
                )
            )
            {
                return false;
            }

            List<int> visibleDeclarations = new();
            foreach ((Match declaration, int nameStart) in declarations)
            {
                Group type = declaration.Groups["type"];
                string typeName = IdentifierValue(type.Value);
                Match[] typeDeclarations = TypeDeclarationRegex
                    .Matches(masked)
                    .Cast<Match>()
                    .Where(match => IdentifierValue(match.Groups["name"].Value) == typeName)
                    .Take(2)
                    .ToArray();
                if (
                    !IsRootTypeQualifier(masked, type.Index)
                    || ContainsNamespaceAlias(masked, typeName)
                    || HasTypeParameterConstraint(masked, typeName)
                    || typeDeclarations.Length != 1
                )
                {
                    return false;
                }
                Match independentType = typeDeclarations[0];
                int body = NextNonWhitespace(
                    masked,
                    independentType.Index + independentType.Length
                );
                if (
                    body < 0
                    || masked[body] != '{'
                    || body == typeStart
                    || FindEnclosingBrace(masked, independentType.Index)
                        != FindEnclosingBrace(masked, useScope)
                    || TypeScopeIsPartial(masked, body)
                    || metadata.UncertainBranches.Exists(branch =>
                        branch.Start <= independentType.Index
                        && independentType.Index < branch.Start + branch.Length
                    )
                )
                {
                    return false;
                }
                if (
                    IsReceiverDeclarationVisible(
                        masked,
                        declaration,
                        nameStart,
                        useScope,
                        usePosition
                    )
                )
                {
                    visibleDeclarations.Add(nameStart);
                }
            }
            return metadata.HasAvailableDeclaration(visibleDeclarations, usePosition);
        }

        private static int FindEnclosingBrace(string masked, int position)
        {
            for (int index = position - 1; 0 <= index; index--)
            {
                switch (masked[index])
                {
                    case '{':
                        return index;
                    case '}':
                        index = FindMatchingBackward(masked, index, '{', '}');
                        break;
                }
            }
            return -1;
        }

        private static List<ParameterList> FindMethodDeclarations(
            string masked,
            string methodName,
            bool requireOverride
        )
        {
            List<ParameterList> declarations = new();
            Regex methodRegex = new(
                @"\b" + Regex.Escape(methodName) + @"\s*\(",
                RegexOptions.CultureInvariant
            );
            foreach (Match match in methodRegex.Matches(masked))
            {
                int nameStart = match.Index;
                int previous = PreviousNonWhitespace(masked, nameStart - 1);
                if (0 <= previous && masked[previous] == '.')
                {
                    continue;
                }

                int open = masked.IndexOf('(', nameStart, match.Length);
                int close = FindMatching(masked, open, '(', ')');
                if (
                    close < 0
                    || !LooksLikeMethodDeclaration(masked, nameStart, close, requireOverride)
                )
                {
                    continue;
                }

                declarations.Add(new ParameterList(nameStart, open + 1, close));
            }
            return declarations;
        }

        private static bool LooksLikeMethodDeclaration(
            string masked,
            int nameStart,
            int close,
            bool requireOverride
        )
        {
            int lineStart = masked.LastIndexOf('\n', nameStart);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            string prefix = masked.Substring(lineStart, nameStart - lineStart);
            if (0 <= prefix.IndexOf('=') || 0 <= prefix.IndexOf("=>", StringComparison.Ordinal))
            {
                return false;
            }
            if (requireOverride && !Regex.IsMatch(prefix, @"\boverride\b"))
            {
                return false;
            }

            int next = NextNonWhitespace(masked, close + 1);
            if (next < 0)
            {
                return false;
            }
            if (masked[next] == '{')
            {
                return true;
            }
            if (next + 1 < masked.Length && masked[next] == '=' && masked[next + 1] == '>')
            {
                return true;
            }
            if (!StartsWithIdentifier(masked, next, "where"))
            {
                return false;
            }

            int body = masked.IndexOf('{', next + "where".Length);
            int expressionBody = masked.IndexOf(
                "=>",
                next + "where".Length,
                StringComparison.Ordinal
            );
            return 0 <= body && (expressionBody < 0 || body < expressionBody)
                || 0 <= expressionBody;
        }

        private static string ScopedMethodKey(string masked, string methodName, int usagePosition)
        {
            return FindContainingTypeStart(masked, usagePosition) + ":" + methodName;
        }

        private static int FindContainingTypeStart(string masked, int position)
        {
            int containingStart = -1;
            foreach (Match match in TypeDeclarationRegex.Matches(masked))
            {
                if (position <= match.Index)
                {
                    break;
                }

                int open = masked.IndexOf('{', match.Index + match.Length);
                if (open < 0 || position <= open)
                {
                    continue;
                }
                int semicolon = masked.IndexOf(';', match.Index + match.Length);
                if (0 <= semicolon && semicolon < open)
                {
                    continue;
                }
                int close = FindMatching(masked, open, '{', '}');
                if (position <= close && containingStart < open)
                {
                    containingStart = open;
                }
            }
            return containingStart;
        }

        private static string GetTypeHeader(string masked, int typeStart)
        {
            foreach (Match match in TypeDeclarationRegex.Matches(masked))
            {
                int afterName = match.Index + match.Length;
                if (masked.IndexOf('{', afterName) == typeStart && 0 <= typeStart)
                {
                    return masked.Substring(afterName, typeStart - afterName);
                }
            }
            return string.Empty;
        }

        private static bool TypeScopeDerivesFromMessageAwareComponent(
            string masked,
            int typeScopeStart,
            SourceMetadata metadata
        )
        {
            if (
                typeScopeStart < 0
                || metadata.DeclaresQualifiedType(masked, "DxMessaging.Unity.MessageAwareComponent")
            )
            {
                return false;
            }
            foreach (Match match in TypeDeclarationRegex.Matches(masked))
            {
                int open = masked.IndexOf('{', match.Index + match.Length);
                if (open != typeScopeStart)
                {
                    continue;
                }
                int baseListStart = match.Index + match.Length;
                Match directBase = Regex.Match(
                    masked.Substring(baseListStart, typeScopeStart - baseListStart),
                    @"\A\s*:\s*(?<qualified>(?<global>global\s*::\s*)?DxMessaging\s*\.\s*Unity\s*\.\s*)?MessageAwareComponent\s*(?=,|\bwhere\b|\z)",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)
                );
                if (
                    !directBase.Success
                    || metadata.UncertainBranches.Exists(branch =>
                        branch.Start < typeScopeStart && match.Index < branch.Start + branch.Length
                    )
                )
                {
                    return false;
                }
                const string PackageType = "DxMessaging.Unity.MessageAwareComponent";
                string spelling = directBase.Groups["qualified"].Success
                    ? PackageType
                    : "MessageAwareComponent";
                if (directBase.Groups["global"].Success)
                {
                    spelling = "global::" + PackageType;
                }
                return IsKnownType(
                    masked,
                    metadata,
                    spelling,
                    PackageType,
                    baseListStart + directBase.Index
                );
            }
            return false;
        }

        private static bool HasNamespaceImport(
            string masked,
            string namespaceName,
            bool hasConditionalDirectives
        )
        {
            if (hasConditionalDirectives)
            {
                return false;
            }
            Match import = Regex.Match(
                masked,
                @"\busing\s+" + Regex.Escape(namespaceName) + @"\s*;",
                RegexOptions.CultureInvariant
            );
            if (!import.Success)
            {
                return false;
            }
            Match namespaceDeclaration = NamespaceDeclarationRegex.Match(masked);
            return FindEnclosingBrace(masked, import.Index) < 0
                && (!namespaceDeclaration.Success || import.Index < namespaceDeclaration.Index);
        }

        private static bool HasTypeOrAliasDeclaration(string masked, string typeName)
        {
            return ContainsNamespaceAlias(masked, typeName)
                || HasTypeParameterConstraint(masked, typeName)
                || Regex
                    .Matches(
                        masked,
                        @"(?<![\w@])(?:(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+|delegate\s+"
                            + SimpleDeclaredTypePattern
                            + @"\s+)(?<name>"
                            + IdentifierSpellingPattern
                            + ")",
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1)
                    )
                    .Cast<Match>()
                    .Any(match => IdentifierValue(match.Groups["name"].Value) == typeName);
        }

        private static bool HasTypeParameterConstraint(string masked, string name)
        {
            return Regex
                .Matches(
                    masked,
                    @"(?<![\w@])where\s+(?<name>" + IdentifierSpellingPattern + @")\s*:",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)
                )
                .Cast<Match>()
                .Any(match => IdentifierValue(match.Groups["name"].Value) == name);
        }

        private static bool IsRootTypeQualifier(string text, int typeStart)
        {
            int prefix = PreviousNonWhitespace(text, typeStart - 1);
            if (prefix < 0)
            {
                return true;
            }
            if (text[prefix] is '.' or '@')
            {
                return false;
            }
            if (text[prefix] != ':')
            {
                return true;
            }
            int earlier = PreviousNonWhitespace(text, prefix - 1);
            return earlier < 0 || text[earlier] != ':';
        }

        private static bool HasProvenTokenReceiver(
            string masked,
            int methodNameStart,
            SourceMetadata metadata
        )
        {
            if (metadata.DeclaresQualifiedType(masked, "DxMessaging.Core.MessageRegistrationToken"))
            {
                return false;
            }
            int dot = PreviousNonWhitespace(masked, methodNameStart - 1);
            if (dot < 0 || masked[dot] != '.')
            {
                return false;
            }
            int receiverEnd = PreviousNonWhitespace(masked, dot - 1);
            int receiverStart = FindIdentifierStart(masked, receiverEnd);
            if (receiverStart < 0)
            {
                return false;
            }

            string receiver = IdentifierValue(
                masked.Substring(receiverStart, receiverEnd - receiverStart + 1)
            );
            int qualifierDot = PreviousNonWhitespace(masked, receiverStart - 1);
            int usageScope = FindContainingTypeStart(masked, methodNameStart);
            bool ownMemberOnly = false;
            if (0 <= qualifierDot && masked[qualifierDot] == '.')
            {
                int qualifier = PreviousNonWhitespace(masked, qualifierDot - 1);
                if (receiver == "Token" && FollowsKeyword(masked, 0, qualifier, "base"))
                {
                    return TypeScopeDerivesFromMessageAwareComponent(masked, usageScope, metadata);
                }
                if (!FollowsKeyword(masked, 0, qualifier, "this"))
                {
                    return false;
                }
                ownMemberOnly = true;
            }

            if (
                !ownMemberOnly
                && receiver == "value"
                && IsImplicitAccessorValue(masked, receiverStart, usageScope)
            )
            {
                return false;
            }

            var declarations = metadata
                .ReceiverDeclarations(masked, receiver)
                .Where(entry =>
                    FindContainingTypeStart(masked, entry.NameStart) == usageScope
                    && (
                        !ownMemberOnly
                        || IsReceiverMember(masked, entry.Declaration, entry.NameStart, usageScope)
                    )
                )
                .ToArray();
            if (
                !ownMemberOnly
                && metadata.HasUnclassifiedReceiverUse(
                    masked,
                    receiver,
                    usageScope,
                    declarations.Select(entry => entry.NameStart)
                )
            )
            {
                return false;
            }
            if (receiver == "Token" && declarations.Length == 0)
            {
                return !TypeScopeIsPartial(masked, usageScope)
                    && TypeScopeDerivesFromMessageAwareComponent(masked, usageScope, metadata);
            }

            HashSet<int> provenNames = new();
            List<int> visibleDeclarations = new();
            foreach (
                (Match match, int nameStart) in metadata.PackageReceiverDeclarations(
                    masked,
                    receiver
                )
            )
            {
                if (
                    FindContainingTypeStart(masked, nameStart) != usageScope
                    || (ownMemberOnly && !IsReceiverMember(masked, match, nameStart, usageScope))
                )
                {
                    continue;
                }
                const string PackageType = "DxMessaging.Core.MessageRegistrationToken";
                Group qualified = match.Groups["qualified"];
                bool isProven;
                if (qualified.Success)
                {
                    string spelling = match.Groups["global"].Success
                        ? "global::" + PackageType
                        : PackageType;
                    isProven =
                        IsRootTypeQualifier(masked, qualified.Index)
                        && IsKnownType(masked, metadata, spelling, PackageType, match.Index);
                }
                else
                {
                    int typePrefix = PreviousNonWhitespace(masked, match.Index - 1);
                    isProven =
                        (typePrefix < 0 || masked[typePrefix] is not ('.' or ':' or '@'))
                        && IsKnownType(
                            masked,
                            metadata,
                            "MessageRegistrationToken",
                            PackageType,
                            match.Index
                        );
                }
                if (!isProven)
                {
                    return false;
                }
                provenNames.Add(nameStart);
                if (
                    IsReceiverDeclarationVisible(
                        masked,
                        match,
                        nameStart,
                        usageScope,
                        methodNameStart
                    )
                )
                {
                    visibleDeclarations.Add(nameStart);
                }
            }
            return metadata.HasAvailableDeclaration(visibleDeclarations, methodNameStart)
                && declarations.All(entry => provenNames.Contains(entry.NameStart));
        }

        private static bool IsImplicitAccessorValue(string masked, int use, int typeScope)
        {
            try
            {
                foreach (
                    Match accessor in ValueAccessorRegex.Matches(masked, Math.Max(0, typeScope))
                )
                {
                    int body = accessor.Groups["body"].Index;
                    if (use <= body)
                    {
                        break;
                    }
                    int previous = PreviousNonWhitespace(masked, accessor.Index - 1);
                    if (
                        previous < 0
                        || (
                            masked[previous] is not ('{' or '}' or ';' or ']')
                            && !FollowsKeyword(masked, 0, previous, "private")
                            && !FollowsKeyword(masked, 0, previous, "protected")
                            && !FollowsKeyword(masked, 0, previous, "internal")
                            && !FollowsKeyword(masked, 0, previous, "public")
                            && !FollowsKeyword(masked, 0, previous, "readonly")
                        )
                    )
                    {
                        continue;
                    }

                    int end =
                        masked[body] == '{'
                            ? FindMatching(masked, body, '{', '}')
                            : FindStatementEnd(masked, body + 2);
                    if (end < 0 || use < end)
                    {
                        return true;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }
            return false;
        }

        private static bool IsReceiverMember(
            string masked,
            Match declaration,
            int nameStart,
            int typeStart
        )
        {
            int declarationStart = declaration.Groups["name"].Index;
            if (
                FindEnclosingBrace(masked, nameStart) != typeStart
                || typeStart < FindEnclosingOpenParenthesis(masked, declarationStart)
            )
            {
                return false;
            }
            int memberStart = masked.LastIndexOfAny(new[] { ';', '{', '}' }, declaration.Index) + 1;
            return masked.IndexOf('=', memberStart, declaration.Index - memberStart) < 0;
        }

        private static bool IsReceiverDeclarationVisible(
            string masked,
            Match declaration,
            int nameStart,
            int typeStart,
            int usePosition
        )
        {
            if (IsReceiverMember(masked, declaration, nameStart, typeStart))
            {
                return true;
            }
            if (usePosition <= nameStart)
            {
                return false;
            }
            int open = FindEnclosingOpenParenthesis(masked, declaration.Groups["name"].Index);
            if (typeStart < open)
            {
                int close = FindMatching(masked, open, '(', ')');
                return 0 <= close
                    && TryFindCallableBody(masked, close, out TextSpan body)
                    && body.Start <= usePosition
                    && usePosition < body.Start + body.Length;
            }
            int block = FindEnclosingBrace(masked, nameStart);
            return typeStart < block && usePosition < FindMatching(masked, block, '{', '}');
        }

        private static int FindIdentifierStart(string text, int end)
        {
            int start = end;
            while (0 <= start && (IsIdentifierPart(text[start]) || text[start] is '@' or '\\'))
            {
                start--;
            }
            start++;
            Match name = DirectMemberIdentifierRegex.Match(text, start, end + 1 - start);
            return name.Success && name.Length == end + 1 - start ? start : -1;
        }

        private static bool InvocationMayNeedMigration(
            string code,
            Invocation invocation,
            string masked,
            SourceMetadata metadata
        )
        {
            foreach (TextSpan callback in GetCallbackArguments(code, invocation))
            {
                string expression = code.Substring(callback.Start, callback.Length);
                if (
                    RefRegex.IsMatch(expression)
                    || MethodGroupMayNeedMigration(code, callback, masked, metadata)
                )
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MethodGroupMayNeedMigration(
            string code,
            TextSpan callback,
            string masked,
            SourceMetadata metadata
        )
        {
            HashSet<string> inspectedNames = new(StringComparer.Ordinal);
            for (
                Match candidate = IdentifierUseRegex.Match(code, callback.Start, callback.Length);
                candidate.Success;
                candidate = candidate.NextMatch()
            )
            {
                if (candidate.Value is "new" or "default" or "null" or "this" or "base")
                {
                    continue;
                }
                string name = IdentifierValue(candidate.Value);
                if (
                    inspectedNames.Add(name)
                    && HasPossibleWritableCallbackDeclaration(masked, metadata, name)
                )
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasPossibleWritableCallbackDeclaration(
            string masked,
            SourceMetadata metadata,
            string name
        )
        {
            foreach (Match candidate in metadata.MaskedIdentifierUses(masked, name))
            {
                int open = NextNonWhitespace(masked, candidate.Index + candidate.Length);
                if (0 <= open && masked[open] == '<')
                {
                    int generic = FindMatching(masked, open, '<', '>');
                    open = generic < 0 ? -1 : NextNonWhitespace(masked, generic + 1);
                }
                if (open < 0 || masked[open] != '(')
                {
                    continue;
                }

                int end = FindMatching(masked, open, '(', ')');
                int following = end < 0 ? -1 : NextNonWhitespace(masked, end + 1);
                if (
                    0 <= following
                    && (
                        masked[following] is '{' or ';'
                        || IsArrow(masked, following)
                        || StartsWithIdentifier(masked, following, "where")
                    )
                    && SplitParameters(masked, open + 1, end)
                        .Any(parameter =>
                        {
                            string type = ReadCallbackRefType(masked, parameter);
                            return type != null && DeclaredCallbackTypeRegex.IsMatch(type);
                        })
                )
                {
                    return true;
                }
            }
            return false;
        }

        private static List<Invocation> FindInvocations(
            string masked,
            HashSet<string> acceptedNames,
            SortedSet<string> manualReview,
            SourceMetadata metadata,
            bool requireProvenReceiver,
            Dictionary<string, string> unprovenMethodGroups = null
        )
        {
            List<Invocation> invocations = new();
            IEnumerable<Match> names = acceptedNames
                .SelectMany(name => metadata.MaskedIdentifierUses(masked, name))
                .OrderBy(match => match.Index);
            foreach (Match match in names)
            {
                string name = IdentifierValue(match.Value);
                if (
                    TryReadRegistrationInvocation(
                        masked,
                        match,
                        name,
                        out Invocation invocation,
                        out var typeArguments
                    )
                )
                {
                    string bindingIssue = null;
                    if (requireProvenReceiver)
                    {
                        if (!HasProvenTokenReceiver(masked, match.Index, metadata))
                        {
                            bindingIssue =
                                "has a receiver whose MessageRegistrationToken type cannot be proven";
                        }
                        else if (
                            !MatchesPackageRegistration(masked, metadata, invocation, typeArguments)
                            || metadata.HasCompetingRegistrationExtension(masked, invocation)
                        )
                        {
                            bindingIssue =
                                "has an overload whose package binding cannot be proven; inspect it manually";
                        }
                    }
                    if (bindingIssue != null)
                    {
                        int preceding = PreviousNonWhitespace(masked, match.Index - 1);
                        if (
                            name == "RegisterGlobalAcceptAll"
                            && 0 <= preceding
                            && masked[preceding] == '.'
                        )
                        {
                            metadata.UnprovenGlobalRegistrations.Add(invocation);
                        }
                        string reason =
                            $"{CallbackLocation(masked, match.Index)} {name} {bindingIssue}";
                        foreach (string group in FindCallbackMethodGroupKeys(masked, invocation))
                        {
                            unprovenMethodGroups?.TryAdd(group, reason);
                        }
                        if (
                            manualReview != null
                            && InvocationMayNeedMigration(masked, invocation, masked, metadata)
                        )
                        {
                            manualReview.Add(reason);
                        }
                        continue;
                    }
                    invocations.Add(invocation);
                }
            }
            if (requireProvenReceiver)
            {
                ReportInterpolatedRegistrations(
                    masked,
                    metadata,
                    acceptedNames,
                    manualReview,
                    unprovenMethodGroups
                );
            }
            return invocations;
        }

        private static bool TryReadRegistrationInvocation(
            string code,
            Match use,
            string name,
            out Invocation invocation,
            out List<TextSpan> typeArguments
        )
        {
            invocation = default;
            typeArguments = new();
            int cursor = NextNonWhitespace(code, use.Index + use.Length);
            if (0 <= cursor && code[cursor] == '<')
            {
                int genericClose = FindMatching(code, cursor, '<', '>');
                if (genericClose < 0)
                {
                    return false;
                }
                typeArguments = SplitParameters(code, cursor + 1, genericClose);
                cursor = NextNonWhitespace(code, genericClose + 1);
            }
            if (cursor < 0 || code[cursor] != '(')
            {
                return false;
            }

            int close = FindMatching(code, cursor, '(', ')');
            if (close < 0 || IsRegistrationDeclaration(code, use, close))
            {
                return false;
            }
            invocation = new Invocation(name, cursor + 1, close);
            return true;
        }

        private static bool IsRegistrationDeclaration(string code, Match name, int close)
        {
            int following = NextNonWhitespace(code, close + 1);
            if (
                following < 0
                || (
                    code[following] != '{'
                    && !IsArrow(code, following)
                    && !StartsWithIdentifier(code, following, "where")
                )
            )
            {
                return false;
            }

            int start = code.LastIndexOfAny(new[] { '{', '}', ';' }, name.Index - 1) + 1;
            start = SkipDeclarationAttributes(code, NextNonWhitespace(code, start));
            if (start < 0 || name.Index < start)
            {
                return false;
            }

            Match declaration = DirectMemberDeclarationRegex.Match(
                code,
                start,
                name.Index + name.Length - start
            );
            return declaration.Success && declaration.Groups["name"].Index == name.Index;
        }

        private static void ReportInterpolatedRegistrations(
            string masked,
            SourceMetadata metadata,
            HashSet<string> acceptedNames,
            SortedSet<string> manualReview,
            Dictionary<string, string> unprovenMethodGroups
        )
        {
            if (metadata.InterpolatedExpressions.Count == 0)
            {
                return;
            }

            foreach (string name in acceptedNames)
            {
                foreach (
                    (string code, Match use, int offset) in metadata.IdentifierUses(masked, name)
                )
                {
                    if (
                        offset == 0
                        || !TryReadRegistrationInvocation(
                            code,
                            use,
                            name,
                            out Invocation invocation,
                            out _
                        )
                    )
                    {
                        continue;
                    }

                    string reason =
                        $"{CallbackLocation(masked, offset + use.Index)} {name} has an interpolated registration whose binding cannot be proven; inspect it manually";
                    bool needsReview = InvocationMayNeedMigration(
                        code,
                        invocation,
                        masked,
                        metadata
                    );
                    foreach (TextSpan callback in GetCallbackArguments(code, invocation))
                    {
                        if (TryGetMethodGroup(code, callback, out string method, out int position))
                        {
                            unprovenMethodGroups?.TryAdd(
                                ScopedMethodKey(masked, method, offset + position),
                                reason
                            );
                            needsReview |= FindMethodDeclarations(
                                    masked,
                                    method,
                                    requireOverride: false
                                )
                                .Exists(declaration =>
                                    HasRefParameter(
                                        masked,
                                        declaration.ParametersStart,
                                        declaration.ParametersEnd
                                    )
                                );
                        }
                    }
                    if (needsReview)
                    {
                        manualReview?.Add(reason);
                    }
                }
            }
        }

        private static bool MatchesPackageRegistration(
            string masked,
            SourceMetadata metadata,
            Invocation invocation,
            List<TextSpan> typeArguments,
            (string MessageType, bool HasContext)? storedDelegate = null
        )
        {
            List<TextSpan> arguments = SplitArguments(masked, invocation.Start, invocation.End);
            string name = invocation.Name;
            if (name == "RegisterGlobalAcceptAll")
            {
                return typeArguments.Count == 0
                    && HasProvenGlobalAcceptAllArguments(masked, metadata, arguments);
            }

            int callbackIndex = CallbackArgumentIndex(name);
            if (
                1 < typeArguments.Count
                || arguments.Count <= callbackIndex
                || callbackIndex + 2 < arguments.Count
            )
            {
                return false;
            }

            string route = name.Contains("Broadcast") ? "Broadcast" : "Targeted";
            if (name.Contains("Untargeted"))
            {
                route = "Untargeted";
            }
            string callbackName = name switch
            {
                "RegisterTargetedWithoutTargeting" => "messageHandler",
                "RegisterTargetedWithoutTargetingPostProcessor" => "postProcessor",
                "RegisterBroadcastWithoutSourcePostProcessor" => "broadcastHandler",
                _ => char.ToLowerInvariant(route[0])
                    + route.Substring(1)
                    + (
                        name.EndsWith("PostProcessor", StringComparison.Ordinal)
                            ? "PostProcessor"
                            : "Handler"
                    ),
            };
            string targetName = route == "Broadcast" ? "source" : "target";
            string targetType = name.Contains("GameObject")
                ? "UnityEngine.GameObject"
                : "DxMessaging.Core.InstanceId";
            if (name.Contains("Component"))
            {
                targetType = "UnityEngine.Component";
            }
            int typeScope = FindContainingTypeStart(masked, invocation.Start);
            string[] roles =
                callbackIndex == 0
                    ? new[] { callbackName, "priority" }
                    : new[] { targetName, callbackName, "priority" };
            bool[] provided = new bool[roles.Length];
            TextSpan callback = default;
            for (int index = 0; index < arguments.Count; index++)
            {
                TextSpan argument = arguments[index];
                int role = index;
                if (TryGetNamedArgument(masked, argument, out string label, out TextSpan value))
                {
                    role = Array.IndexOf(roles, label);
                    argument = value;
                }
                if (role < 0 || provided[role])
                {
                    return false;
                }
                provided[role] = true;
                if (role == callbackIndex)
                {
                    callback = argument;
                }
                else if (
                    !HasProvenRegistrationValue(
                        masked,
                        metadata,
                        argument,
                        typeScope,
                        roles[role] == "priority" ? "System.Int32" : targetType
                    )
                )
                {
                    return false;
                }
            }
            if (!provided[callbackIndex] || (callbackIndex == 1 && !provided[0]))
            {
                return false;
            }
            if (
                storedDelegate == null
                && TryReadExplicitPackageDelegate(
                    masked,
                    metadata,
                    callback,
                    out var explicitDelegate
                )
            )
            {
                storedDelegate = explicitDelegate;
            }
            string messageType;
            if (typeArguments.Count == 1)
            {
                TextSpan typeArgument = typeArguments[0];
                messageType = CompactType(
                    masked.Substring(typeArgument.Start, typeArgument.Length)
                );
            }
            else if (storedDelegate is { } declared)
            {
                messageType = declared.MessageType;
            }
            else
            {
                int open = FindCallbackParametersOpen(masked, metadata, callback);
                int close = open < 0 ? -1 : FindMatching(masked, open, '(', ')');
                if (close < 0)
                {
                    return false;
                }
                List<TextSpan> parameters = SplitParameters(masked, open + 1, close);
                messageType = ReadCallbackRefType(masked, parameters[^1]);
                if (messageType == null)
                {
                    return false;
                }
                messageType = CompactType(messageType);
            }

            bool hasContext = name.Contains("WithoutSource") || name.Contains("WithoutTargeting");
            return HasProvenRouteMessage(
                    masked,
                    metadata,
                    messageType,
                    route,
                    invocation.Start,
                    typeScope
                )
                && (
                    storedDelegate is { } stored
                        ? stored.MessageType == messageType && stored.HasContext == hasContext
                        : HasProvenCallbackParameters(
                            masked,
                            metadata,
                            callback,
                            messageType,
                            hasContext
                        )
                );
        }

        private static bool HasProvenGlobalAcceptAllArguments(
            string masked,
            SourceMetadata metadata,
            List<TextSpan> arguments
        )
        {
            if (arguments.Count != 3)
            {
                return false;
            }
            string[] roles = { "Untargeted", "Targeted", "Broadcast" };
            bool[] provided = new bool[roles.Length];
            for (int index = 0; index < arguments.Count; index++)
            {
                TextSpan argument = arguments[index];
                int role = index;
                if (TryGetNamedArgument(masked, argument, out string label, out TextSpan value))
                {
                    role = Array.FindIndex(roles, name => label == "acceptAll" + name);
                    argument = value;
                }
                if (role < 0 || provided[role])
                {
                    return false;
                }
                provided[role] = true;
                string marker = "DxMessaging.Core.Messages.I" + roles[role] + "Message";
                if (
                    !HasProvenCallbackParameters(
                        masked,
                        metadata,
                        argument,
                        marker,
                        role != 0,
                        knownMessage: true
                    )
                )
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasProvenCallbackParameters(
            string masked,
            SourceMetadata metadata,
            TextSpan callback,
            string messageType,
            bool withContext,
            bool knownMessage = false
        )
        {
            int open = FindCallbackParametersOpen(masked, metadata, callback);
            int close = open < 0 ? -1 : FindMatching(masked, open, '(', ')');
            if (close < 0 || !HasRefParameter(masked, open + 1, close))
            {
                // No ref parameter list was proposed in this source.
                return true;
            }
            List<TextSpan> parameters = SplitParameters(masked, open + 1, close);
            if (
                parameters.Count != (withContext ? 2 : 1)
                || !HasProvenVoidCallback(masked, metadata, callback, open, close)
            )
            {
                return false;
            }
            string actualType = ReadCallbackRefType(masked, parameters[^1]);
            if (actualType == null)
            {
                return false;
            }
            string actualMessage = CompactType(actualType);
            if (
                knownMessage
                    ? !IsKnownType(
                        masked,
                        metadata,
                        actualMessage,
                        messageType,
                        parameters[^1].Start
                    )
                    : actualMessage != messageType
            )
            {
                return false;
            }
            if (!withContext)
            {
                return true;
            }
            string context = ReadCallbackRefType(masked, parameters[0]);
            return context != null
                && IsKnownType(
                    masked,
                    metadata,
                    CompactType(context),
                    "DxMessaging.Core.InstanceId",
                    parameters[0].Start
                );
        }

        private static bool HasProvenVoidCallback(
            string masked,
            SourceMetadata metadata,
            TextSpan callback,
            int open,
            int close
        )
        {
            Invocation expression = new(
                "callback",
                callback.Start,
                callback.Start + callback.Length
            );
            if (!TryGetInlineDelegateParameters(masked, expression, out _))
            {
                int methodName = FindIdentifierStart(
                    masked,
                    PreviousNonWhitespace(masked, open - 1)
                );
                int returnType = PreviousNonWhitespace(masked, methodName - 1);
                return FollowsKeyword(masked, 0, returnType, "void");
            }
            int first = NextNonWhitespace(masked, close + 1);
            if (0 <= first && IsArrow(masked, first))
            {
                first = NextNonWhitespace(masked, first + 2);
            }
            if (first < 0 || callback.Start + callback.Length <= first)
            {
                return false;
            }
            if (masked[first] == '{')
            {
                int endOfBlock = FindMatching(masked, first, '{', '}');
                if (endOfBlock < 0 || callback.Start + callback.Length <= endOfBlock)
                {
                    return false;
                }
                for (
                    Match token = IdentifierUseRegex.Match(
                        masked,
                        first + 1,
                        endOfBlock - first - 1
                    );
                    token.Success;
                    token = token.NextMatch()
                )
                {
                    if (token.Value == "return")
                    {
                        int next = NextAfterComments(
                            metadata.Source,
                            token.Index + token.Length,
                            endOfBlock
                        );
                        if (next < 0 || endOfBlock <= next || metadata.Source[next] != ';')
                        {
                            return false;
                        }
                    }
                }
                return true;
            }

            if (!TryFindCallableBody(masked, close, out TextSpan body))
            {
                return false;
            }
            int start = NextNonWhitespace(masked, body.Start);
            int end = PreviousNonWhitespace(masked, body.Start + body.Length - 1);
            return 0 <= start
                && start <= end
                && (
                    IsStatementInvocation(masked, start, end)
                    || IsStatementWrite(masked, start, end)
                );
        }

        private static bool IsStatementInvocation(string text, int start, int end)
        {
            if (text[end] != ')')
            {
                return false;
            }
            int open = FindMatchingBackward(text, end, '(', ')', start);
            if (open <= start)
            {
                return false;
            }
            string target = text.Substring(start, open - start).Trim();
            return target
                    is not (
                        "checked"
                        or "unchecked"
                        or "sizeof"
                        or "typeof"
                        or "default"
                        or "nameof"
                        or "__makeref"
                        or "__refvalue"
                        or "__reftype"
                    )
                && InvocationTargetRegex.IsMatch(target)
                && HasInvocationTypeArguments(text, start, open);
        }

        private static bool HasInvocationTypeArguments(string text, int start, int open)
        {
            for (int index = start; index < open; index++)
            {
                if (
                    text[index] == '<'
                    && !IsGenericOpenAngle(text, index, open, allowClosingAngleFollower: true)
                )
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsStatementWrite(string text, int start, int end)
        {
            if (start < end && text[start] is '+' or '-' && text[start + 1] == text[start])
            {
                return ReadStorageTarget(text, start + 2, end) > end;
            }

            int following = ReadStorageTarget(text, start, end);
            if (following < 0 || end < following)
            {
                return false;
            }

            Match write = DirectWriteOperatorRegex.Match(text, following, end - following + 1);
            if (!write.Success)
            {
                return false;
            }
            if (write.Value is "++" or "--")
            {
                return following + write.Length == end + 1;
            }
            return following + write.Length <= end;
        }

        // The writable operand must start at the outside of the expression.
        // An assignment nested in a lambda or a query is not a statement expression for a void delegate.
        private static int ReadStorageTarget(string text, int start, int end)
        {
            int cursor = NextNonWhitespace(text, start);
            if (cursor < 0 || end < cursor)
            {
                return -1;
            }

            if (text[cursor] == '(')
            {
                int close = FindMatching(text, cursor, '(', ')');
                if (
                    close < 0
                    || end < close
                    || !SplitArguments(text, cursor + 1, close)
                        .TrueForAll(element =>
                        {
                            int elementEnd = element.Start + element.Length - 1;
                            return ReadStorageTarget(text, element.Start, elementEnd) > elementEnd;
                        })
                )
                {
                    return -1;
                }
                cursor = close + 1;
            }
            else
            {
                cursor = ReadStorageName(text, cursor, end);
            }

            while (0 <= cursor && cursor <= end)
            {
                cursor = NextNonWhitespace(text, cursor);
                if (cursor < 0 || end < cursor)
                {
                    return end + 1;
                }

                char token = text[cursor];
                if (token == '!' && (end == cursor || text[cursor + 1] != '='))
                {
                    cursor++;
                    continue;
                }
                if (token == '.' || (token == ':' && cursor < end && text[cursor + 1] == ':'))
                {
                    int name = NextNonWhitespace(text, cursor + (token == ':' ? 2 : 1));
                    cursor = 0 <= name && name <= end ? ReadStorageName(text, name, end) : -1;
                    continue;
                }

                char closer = token switch
                {
                    '(' => ')',
                    '[' => ']',
                    '<' when IsGenericOpenAngle(text, cursor, end + 1) => '>',
                    _ => '\0',
                };
                if (closer == '\0')
                {
                    return cursor;
                }
                int close = FindMatching(text, cursor, token, closer);
                if (close < 0 || end < close)
                {
                    return -1;
                }
                cursor = close + 1;
            }
            return cursor < 0 ? -1 : end + 1;
        }

        private static int ReadStorageName(string text, int start, int end)
        {
            Match name = DirectMemberIdentifierRegex.Match(text, start);
            return name.Success && start + name.Length <= end + 1 ? start + name.Length : -1;
        }

        private static string ReadCallbackRefType(string masked, TextSpan parameter)
        {
            int modifier = FindRefParameterModifier(masked, parameter);
            int nameEnd = PreviousNonWhitespace(masked, parameter.Start + parameter.Length - 1);
            int name = FindIdentifierStart(masked, nameEnd);
            int start = modifier < 0 ? -1 : NextNonWhitespace(masked, modifier + "ref".Length);
            return 0 <= start && start < name ? masked.Substring(start, name - start).Trim() : null;
        }

        private static int FindFastDelegateName(string masked, int start, int end)
        {
            if (end <= start)
            {
                return -1;
            }
            for (
                Match name = IdentifierUseRegex.Match(masked, start, end - start);
                name.Success;
                name = name.NextMatch()
            )
            {
                if (IdentifierValue(name.Value) is "FastHandler" or "FastHandlerWithContext")
                {
                    return name.Index;
                }
            }
            return -1;
        }

        private static bool TryReadExplicitPackageDelegate(
            string masked,
            SourceMetadata metadata,
            TextSpan callback,
            out (string MessageType, bool HasContext) signature
        )
        {
            signature = default;
            Invocation expression = new(
                "callback",
                callback.Start,
                callback.Start + callback.Length
            );
            int prefixEnd;
            if (TryGetInlineDelegateParameters(masked, expression, out TextSpan parameters))
            {
                prefixEnd = parameters.Start - 1;
            }
            else if (!TryGetMethodGroup(masked, callback, out _, out prefixEnd))
            {
                return false;
            }

            int handler = FindFastDelegateName(masked, callback.Start, prefixEnd);
            if (
                handler < 0
                || !TryClassifyFastDelegateType(masked, handler, metadata, out bool proven, out _)
                || !proven
            )
            {
                return false;
            }

            Match typeName = DirectMemberIdentifierRegex.Match(masked, handler);
            int open = NextNonWhitespace(masked, handler + typeName.Length);
            int close = FindMatching(masked, open, '<', '>');
            if (close < 0 || prefixEnd <= close)
            {
                return false;
            }
            signature = (
                CompactType(masked.Substring(open + 1, close - open - 1)),
                IdentifierValue(typeName.Value) == "FastHandlerWithContext"
            );
            return true;
        }

        private static bool HasProvenRouteMessage(
            string masked,
            SourceMetadata metadata,
            string type,
            string route,
            int use,
            int typeScope
        )
        {
            string marker = "DxMessaging.Core.Messages.I" + route + "Message";
            if (
                IsKnownRouteMarkerType(masked, metadata, type, marker, use)
                || (
                    route == "Untargeted"
                    && IsKnownType(
                        masked,
                        metadata,
                        type,
                        "DxMessaging.Core.Messages.GlobalStringMessage",
                        use
                    )
                )
                || (
                    route == "Targeted"
                    && IsKnownType(
                        masked,
                        metadata,
                        type,
                        "DxMessaging.Core.Messages.StringMessage",
                        use
                    )
                )
            )
            {
                return true;
            }
            int generic = type.IndexOf('<');
            if (0 <= generic && FindMatching(type, generic, '<', '>') != type.Length - 1)
            {
                return false;
            }
            string name = generic < 0 ? type : type.Substring(0, generic);
            if (HasRouteTypeConstraint(masked, metadata, name, marker, use))
            {
                return true;
            }
            if (HasTypeParameterConstraint(masked, name) || ContainsNamespaceAlias(masked, name))
            {
                return false;
            }
            string useNamespace = NamespaceAt(masked, use);
            bool globallyQualified = name.StartsWith("global::", StringComparison.Ordinal);
            if (globallyQualified)
            {
                name = name.Substring("global::".Length);
            }
            else if (
                name.Contains('.')
                || 0 <= FindTypeParameterDeclaration(masked, name, use)
                || (0 <= typeScope && TypeScopeIsPartial(masked, typeScope))
            )
            {
                return false;
            }
            int typeArity = generic < 0 ? 0 : ReadTypeArity(type, generic);
            string simpleName = name.Substring(name.LastIndexOf('.') + 1);
            Match[] declarations = TypeDeclarationRegex
                .Matches(masked)
                .Cast<Match>()
                .Where(declaration =>
                    IdentifierValue(declaration.Groups["name"].Value) == simpleName
                    && DeclarationArity(masked, declaration) == typeArity
                )
                .Take(2)
                .ToArray();
            if (declarations.Length == 0)
            {
                string qualified =
                    globallyQualified || useNamespace.Length == 0
                        ? name
                        : useNamespace + "." + name;
                return (
                        globallyQualified
                        || (
                            !HasUnprovenTypeScope(masked, metadata, typeScope, -1)
                            && !metadata.HasCompetingTypeAtUse(
                                masked,
                                name,
                                typeArity,
                                use,
                                knownQualifiedType: qualified
                            )
                        )
                    )
                    && metadata.PackageBindingIssue(marker, use) == null
                    && metadata.HasProjectRoute(qualified, typeArity, marker);
            }
            if (declarations.Length != 1)
            {
                return false;
            }

            Match match = declarations[0];
            int declarationScope = FindContainingTypeStart(masked, match.Index);
            string declarationNamespace = NamespaceAt(masked, match.Index);
            if (globallyQualified)
            {
                string qualified =
                    declarationNamespace.Length == 0
                        ? simpleName
                        : declarationNamespace + "." + simpleName;
                if (declarationScope != -1 || qualified != name)
                {
                    return false;
                }
            }
            else if (
                declarationNamespace != useNamespace
                || (declarationScope != -1 && declarationScope != typeScope)
                || HasUnprovenTypeScope(masked, metadata, typeScope, declarationScope)
            )
            {
                return false;
            }

            int body = masked.IndexOf('{', match.Index + match.Length);
            return HasDirectRouteMarker(masked, metadata, match, body, marker);
        }

        private static bool HasDirectRouteMarker(
            string masked,
            SourceMetadata metadata,
            Match declaration,
            int body,
            string marker
        )
        {
            int colon = NextNonWhitespace(masked, declaration.Index + declaration.Length);
            if (
                body < 0
                || colon < 0
                || body <= colon
                || masked[colon] != ':'
                || metadata.UncertainBranches.Exists(branch =>
                    branch.Start < body && declaration.Index < branch.Start + branch.Length
                )
            )
            {
                return false;
            }
            int where = masked.IndexOf(" where ", colon, body - colon, StringComparison.Ordinal);
            int end = where < 0 ? body : where;
            return SplitParameters(masked, colon + 1, end)
                .Any(baseType => IsRouteMarker(masked, metadata, baseType, marker));
        }

        private static bool HeaderMayDeclareRoute(string masked, Match declaration, int body)
        {
            int colon = NextNonWhitespace(masked, declaration.Index + declaration.Length);
            if (colon < 0 || body <= colon || masked[colon] != ':')
            {
                return false;
            }
            return masked.IndexOf("Message", colon, body - colon, StringComparison.Ordinal) >= 0;
        }

        private static bool IsFileLocalDeclaration(string masked, Match declaration)
        {
            int prefix = PreviousNonWhitespace(masked, declaration.Index - 1);
            while (0 <= prefix)
            {
                int start = FindIdentifierStart(masked, prefix);
                if (start < 0)
                {
                    return false;
                }
                string modifier = masked.Substring(start, prefix - start + 1);
                if (modifier == "file")
                {
                    return true;
                }
                if (
                    modifier
                    is not (
                        "partial"
                        or "unsafe"
                        or "readonly"
                        or "ref"
                        or "abstract"
                        or "sealed"
                        or "static"
                        or "new"
                    )
                )
                {
                    return false;
                }
                prefix = PreviousNonWhitespace(masked, start - 1);
            }
            return false;
        }

        private static bool HasUnprovenTypeScope(
            string masked,
            SourceMetadata metadata,
            int scope,
            int declarationScope
        )
        {
            for (
                int current = scope;
                0 <= current && current != declarationScope;
                current = FindContainingTypeStart(masked, current)
            )
            {
                if (
                    TypeScopeIsPartial(masked, current)
                    || (
                        GetTypeHeader(masked, current).Contains(':')
                        && !TypeScopeDerivesFromMessageAwareComponent(masked, current, metadata)
                    )
                )
                {
                    return true;
                }
            }
            return false;
        }

        private static int DeclarationArity(string masked, Match declaration)
        {
            Group name = declaration.Groups["name"];
            int open = NextNonWhitespace(masked, name.Index + name.Length);
            return 0 <= open && open < declaration.Index + declaration.Length && masked[open] == '<'
                ? ReadTypeArity(masked, open)
                : 0;
        }

        private static int ReadTypeArity(string text, int open)
        {
            int close = FindMatching(text, open, '<', '>');
            return close < 0 ? -1 : SplitParameters(text, open + 1, close).Count;
        }

        private static int FindTypeParameterDeclaration(string masked, string name, int use)
        {
            string identifier = IdentifierValue(name);
            for (
                int open = 0 < use ? masked.LastIndexOf('<', use - 1) : -1;
                0 <= open;
                open = 0 < open ? masked.LastIndexOf('<', open - 1) : -1
            )
            {
                int nameEnd = PreviousNonWhitespace(masked, open - 1);
                if (
                    nameEnd < 0
                    || FindIdentifierStart(masked, nameEnd) < 0
                    || !HasTypeParameter(masked, open, identifier, out int close)
                )
                {
                    continue;
                }

                int next = NextNonWhitespace(masked, close + 1);
                if (next < 0 || use <= next)
                {
                    continue;
                }
                if (masked[next] == '(')
                {
                    int parametersEnd = FindMatching(masked, next, '(', ')');
                    if (
                        0 <= parametersEnd
                        && TryFindCallableBody(masked, parametersEnd, out TextSpan body)
                        && next <= use
                        && use < body.Start + body.Length
                    )
                    {
                        return open;
                    }
                }

                foreach (Match declaration in TypeDeclarationRegex.Matches(masked))
                {
                    Group type = declaration.Groups["name"];
                    if (type.Index + type.Length != nameEnd + 1)
                    {
                        continue;
                    }
                    int body = masked.IndexOf('{', close + 1);
                    if (0 <= body && body < use && use < FindMatching(masked, body, '{', '}'))
                    {
                        return open;
                    }
                    break;
                }
            }
            return -1;
        }

        private static bool HasTypeParameter(string masked, int open, string name, out int close)
        {
            bool found = false;
            close = -1;
            int cursor = open;
            while (0 <= cursor)
            {
                cursor = SkipDeclarationAttributes(masked, NextNonWhitespace(masked, cursor + 1));
                Match parameter =
                    cursor < 0 ? Match.Empty : DirectMemberIdentifierRegex.Match(masked, cursor);
                if (parameter.Success && parameter.Value is "in" or "out")
                {
                    cursor = NextNonWhitespace(masked, cursor + parameter.Length);
                    parameter =
                        cursor < 0
                            ? Match.Empty
                            : DirectMemberIdentifierRegex.Match(masked, cursor);
                }
                if (!parameter.Success)
                {
                    return false;
                }
                found |= IdentifierValue(parameter.Value) == name;
                cursor = NextNonWhitespace(masked, cursor + parameter.Length);
                if (0 <= cursor && masked[cursor] == '>')
                {
                    close = cursor;
                    return found;
                }
                if (cursor < 0 || masked[cursor] != ',')
                {
                    return false;
                }
            }
            return false;
        }

        private static bool HasRouteTypeConstraint(
            string masked,
            SourceMetadata metadata,
            string name,
            string marker,
            int use
        )
        {
            if (!IdentifierRegex.IsMatch(name))
            {
                return false;
            }
            int body = FindEnclosingBrace(masked, use);
            if (body < 0)
            {
                return false;
            }
            int headerStart = masked.LastIndexOfAny(new[] { '{', '}', ';' }, body - 1) + 1;
            if (
                metadata.UncertainBranches.Exists(branch =>
                    branch.Start < body && headerStart < branch.Start + branch.Length
                )
            )
            {
                return false;
            }
            int declaration = FindTypeParameterDeclaration(masked, name, use);
            if (declaration < headerStart || body <= declaration)
            {
                return false;
            }
            string header = masked.Substring(headerStart, body - headerStart);
            Match constraint = Regex.Match(
                header,
                @"\bwhere\s+" + Regex.Escape(name) + @"\s*:\s*(?<types>[^;{}]+?)(?=\bwhere\b|\z)",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            );
            if (!constraint.Success)
            {
                return false;
            }
            Group types = constraint.Groups["types"];
            return SplitParameters(header, types.Index, types.Index + types.Length)
                .Any(type => IsRouteMarker(header, metadata, type, marker, masked, headerStart));
        }

        private static bool IsRouteMarker(
            string text,
            SourceMetadata metadata,
            TextSpan span,
            string marker,
            string source = null,
            int offset = 0
        )
        {
            string name = CompactType(text.Substring(span.Start, span.Length));
            return IsKnownRouteMarkerType(
                source ?? text,
                metadata,
                name,
                marker,
                span.Start + offset
            );
        }

        private static bool IsKnownRouteMarkerType(
            string masked,
            SourceMetadata metadata,
            string spelling,
            string marker,
            int use
        )
        {
            int generic = spelling.IndexOf('<');
            if (generic < 0)
            {
                return IsKnownType(masked, metadata, spelling, marker, use);
            }
            return FindMatching(spelling, generic, '<', '>') == spelling.Length - 1
                && ReadTypeArity(spelling, generic) == 1
                && IsKnownType(
                    masked,
                    metadata,
                    spelling.Substring(0, generic),
                    marker,
                    use,
                    arity: 1
                );
        }

        private static string NamespaceAt(string masked, int position)
        {
            List<string> parts = new();
            foreach (Match declaration in NamespaceDeclarationRegex.Matches(masked))
            {
                if (position < declaration.Index + declaration.Length)
                {
                    break;
                }
                int delimiter = declaration.Index + declaration.Length - 1;
                if (
                    masked[delimiter] == ';'
                    || position < FindMatching(masked, delimiter, '{', '}')
                )
                {
                    parts.Add(NormalizeNamespacePath(declaration.Groups["path"].Value));
                }
            }
            return string.Join(".", parts);
        }

        private static string NormalizeNamespacePath(string path) =>
            string.Join(
                ".",
                IdentifierUseRegex
                    .Matches(path)
                    .Cast<Match>()
                    .Select(part => IdentifierValue(part.Value))
            );

        private static string SimpleTypeName(string qualifiedName) =>
            qualifiedName.Substring(qualifiedName.LastIndexOf('.') + 1);

        private static string CompactType(string text) =>
            new string(text.Where(character => !char.IsWhiteSpace(character)).ToArray());

        private static bool IsKnownType(
            string masked,
            SourceMetadata metadata,
            string spelling,
            string qualifiedName,
            int use,
            int arity = 0
        )
        {
            if (metadata.DeclaresQualifiedType(masked, qualifiedName, arity))
            {
                return false;
            }
            if (spelling != "global::" + qualifiedName)
            {
                int dot = qualifiedName.LastIndexOf('.');
                string name = qualifiedName.Substring(dot + 1);
                if (spelling == qualifiedName)
                {
                    if (
                        metadata.HasNamespaceShadow(
                            masked,
                            qualifiedName.Substring(0, qualifiedName.IndexOf('.'))
                        )
                    )
                    {
                        return false;
                    }
                }
                else if (
                    spelling != name
                    || !HasNamespaceImport(
                        masked,
                        qualifiedName.Substring(0, dot),
                        metadata.HasConditionalDirectives
                    )
                    || HasTypeOrAliasDeclaration(masked, name)
                    || 0 <= FindTypeParameterDeclaration(masked, name, use)
                )
                {
                    return false;
                }
                if (metadata.HasCompetingTypeAtUse(masked, spelling, arity, use))
                {
                    return false;
                }
            }
            return metadata.PackageBindingIssue(qualifiedName, use) == null;
        }

        private static bool HasProvenRegistrationValue(
            string masked,
            SourceMetadata metadata,
            TextSpan value,
            int typeScope,
            string requiredType
        )
        {
            int valueStart = NextNonWhitespace(masked, value.Start);
            string expression = masked.Substring(value.Start, value.Length).Trim();
            while (
                expression.Length != 0
                && expression[0] == '('
                && FindMatching(expression, 0, '(', ')') == expression.Length - 1
            )
            {
                expression = expression.Substring(1, expression.Length - 2).Trim();
                valueStart = NextNonWhitespace(masked, valueStart + 1);
            }
            if (expression == "default")
            {
                return true;
            }
            if (
                requiredType == "System.Int32"
                && DecimalIntLiteralRegex.IsMatch(expression)
                && int.TryParse(
                    expression.Replace("_", ""),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out _
                )
            )
            {
                return true;
            }

            if (
                TrySplitConditionalRegistrationValue(
                    masked,
                    value,
                    out TextSpan whenTrue,
                    out TextSpan whenFalse
                )
            )
            {
                return HasProvenRegistrationValue(
                        masked,
                        metadata,
                        whenTrue,
                        typeScope,
                        requiredType
                    )
                    && HasProvenRegistrationValue(
                        masked,
                        metadata,
                        whenFalse,
                        typeScope,
                        requiredType
                    );
            }

            string explicitType = FindExplicitValueType(expression);
            if (explicitType != null)
            {
                return CanPassRegistrationValue(
                    masked,
                    metadata,
                    explicitType,
                    requiredType,
                    value.Start
                );
            }
            if (TypeScopeDerivesFromMessageAwareComponent(masked, typeScope, metadata))
            {
                if (expression == "this")
                {
                    return CanPassRegistrationValue(
                        masked,
                        metadata,
                        "global::UnityEngine.Component",
                        requiredType,
                        value.Start
                    );
                }
                if (
                    expression is "gameObject" or "this.gameObject"
                    && !TypeScopeIsPartial(masked, typeScope)
                    && !metadata.ReceiverDeclarations(masked, "gameObject").Any()
                    && !metadata.HasUnclassifiedReceiverUse(
                        masked,
                        "gameObject",
                        typeScope,
                        Array.Empty<int>()
                    )
                )
                {
                    return CanPassRegistrationValue(
                        masked,
                        metadata,
                        "global::UnityEngine.GameObject",
                        requiredType,
                        value.Start
                    );
                }
            }

            int dot = expression.IndexOf('.');
            bool ownMemberOnly = 0 <= dot && expression.Substring(0, dot).Trim() == "this";
            int nameStart = ownMemberOnly ? NextNonWhitespace(expression, dot + 1) : 0;
            if (nameStart < 0)
            {
                return false;
            }

            Match identifier = DirectMemberIdentifierRegex.Match(expression, nameStart);
            if (!identifier.Success || nameStart + identifier.Length != expression.Length)
            {
                return false;
            }

            string variable = IdentifierValue(identifier.Value);
            valueStart += nameStart;
            if (
                !ownMemberOnly
                && variable == "value"
                && IsImplicitAccessorValue(masked, valueStart, typeScope)
            )
            {
                return false;
            }
            var declarations = metadata
                .ReceiverDeclarations(masked, variable)
                .Where(entry =>
                    entry.NameStart != valueStart
                    && FindContainingTypeStart(masked, entry.NameStart) == typeScope
                    && (
                        !ownMemberOnly
                        || IsReceiverMember(masked, entry.Declaration, entry.NameStart, typeScope)
                    )
                )
                .ToArray();
            if (
                !ownMemberOnly
                && metadata.HasUnclassifiedReceiverUse(
                    masked,
                    variable,
                    typeScope,
                    declarations.Select(entry => entry.NameStart).Append(valueStart)
                )
            )
            {
                return false;
            }

            List<int> visibleDeclarations = new();
            foreach ((Match declaration, int name) in declarations)
            {
                Group type = declaration.Groups["type"];
                Group firstName = declaration.Groups["name"];
                if (
                    NextNonWhitespace(masked, type.Index + type.Length) != firstName.Index
                    || !CanPassRegistrationValue(
                        masked,
                        metadata,
                        ReadQualifiedType(masked, type),
                        requiredType,
                        type.Index
                    )
                )
                {
                    return false;
                }
                if (IsReceiverDeclarationVisible(masked, declaration, name, typeScope, value.Start))
                {
                    visibleDeclarations.Add(name);
                }
            }
            return metadata.HasAvailableDeclaration(visibleDeclarations, value.Start);
        }

        private static bool TrySplitConditionalRegistrationValue(
            string text,
            TextSpan value,
            out TextSpan whenTrue,
            out TextSpan whenFalse
        )
        {
            whenTrue = default;
            whenFalse = default;
            int question = -1;
            int depth = 0;
            int end = value.Start + value.Length;
            for (int index = value.Start; index < end; index++)
            {
                char token = text[index];
                char close = token switch
                {
                    '(' => ')',
                    '[' => ']',
                    '{' => '}',
                    '<' when IsGenericOpenAngle(text, index, end) => '>',
                    _ => '\0',
                };
                if (close != '\0')
                {
                    index = FindMatching(text, index, token, close);
                    if (index < 0 || end <= index)
                    {
                        return false;
                    }
                    continue;
                }
                if (token == '?')
                {
                    int next = NextNonWhitespace(text, index + 1);
                    if (0 <= next && next < end && text[next] is '?' or '.' or '[')
                    {
                        index = text[next] == '[' ? next - 1 : next;
                        continue;
                    }
                    if (depth++ == 0)
                    {
                        question = index;
                    }
                }
                else if (token == ':' && 0 < depth)
                {
                    if (index + 1 < end && text[index + 1] == ':')
                    {
                        index++;
                        continue;
                    }
                    if (--depth == 0)
                    {
                        whenTrue = new TextSpan(question + 1, index - question - 1);
                        whenFalse = new TextSpan(index + 1, end - index - 1);
                        return true;
                    }
                }
            }
            return false;
        }

        private static string FindExplicitValueType(string expression)
        {
            if (expression.Length == 0)
            {
                return null;
            }
            if (expression[0] == '(')
            {
                int cast = FindMatching(expression, 0, '(', ')');
                int operand = cast < 0 ? -1 : NextNonWhitespace(expression, cast + 1);
                if (operand < 0)
                {
                    return null;
                }

                Match identifier = IdentifierUseRegex.Match(expression, operand);
                if (
                    (
                        identifier.Success
                        && identifier.Index == operand
                        && identifier.Index + identifier.Length == expression.Length
                    )
                    || (
                        expression[operand] == '('
                        && FindMatching(expression, operand, '(', ')') == expression.Length - 1
                    )
                )
                {
                    return CompactType(expression.Substring(1, cast - 1));
                }
                return null;
            }
            int open = expression.IndexOf('(');
            if (open < 0 || FindMatching(expression, open, '(', ')') != expression.Length - 1)
            {
                return null;
            }
            if (StartsWithIdentifier(expression, 0, "new"))
            {
                return CompactType(expression.Substring("new".Length, open - "new".Length));
            }
            return StartsWithIdentifier(expression, 0, "default")
                ? CompactType(expression.Substring(open + 1, expression.Length - open - 2))
                : null;
        }

        private static bool CanPassRegistrationValue(
            string masked,
            SourceMetadata metadata,
            string spelling,
            string requiredType,
            int use
        )
        {
            if (requiredType == "System.Int32" && spelling == "int")
            {
                return true;
            }
            return IsKnownType(masked, metadata, spelling, requiredType, use)
                || (
                    requiredType == "DxMessaging.Core.InstanceId"
                    && (
                        IsKnownType(masked, metadata, spelling, "UnityEngine.GameObject", use)
                        || IsKnownType(masked, metadata, spelling, "UnityEngine.Component", use)
                    )
                );
        }

        private static string ReadQualifiedType(string masked, Group terminal)
        {
            int start = terminal.Index;
            while (0 <= start)
            {
                int separator = PreviousNonWhitespace(masked, start - 1);
                if (separator < 0)
                {
                    break;
                }
                int prefix;
                if (masked[separator] == '.')
                {
                    prefix = separator - 1;
                }
                else if (1 <= separator && masked[separator] == ':' && masked[separator - 1] == ':')
                {
                    prefix = separator - 2;
                }
                else
                {
                    break;
                }
                int qualifier = FindIdentifierStart(masked, PreviousNonWhitespace(masked, prefix));
                if (qualifier < 0)
                {
                    break;
                }
                start = qualifier;
            }
            return CompactType(masked.Substring(start, terminal.Index + terminal.Length - start));
        }

        private static HashSet<string> CollectCallbackMethodGroups(
            string masked,
            List<Invocation> invocations
        )
        {
            return invocations
                .SelectMany(invocation => FindCallbackMethodGroupKeys(masked, invocation))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static IEnumerable<string> FindCallbackMethodGroupKeys(
            string masked,
            Invocation invocation
        )
        {
            foreach (TextSpan callback in GetCallbackArguments(masked, invocation))
            {
                if (TryGetMethodGroup(masked, callback, out string methodName, out _))
                {
                    yield return ScopedMethodKey(masked, methodName, callback.Start);
                }
            }
        }

        private static IEnumerable<TextSpan> GetCallbackArguments(
            string masked,
            Invocation invocation
        )
        {
            List<TextSpan> arguments = SplitArguments(masked, invocation.Start, invocation.End);
            if (invocation.Name == "RegisterGlobalAcceptAll")
            {
                if (arguments.Count != 3)
                {
                    yield break;
                }
                foreach (TextSpan argument in arguments)
                {
                    yield return GetArgumentValueSpan(masked, argument);
                }
                yield break;
            }

            foreach (TextSpan argument in arguments)
            {
                if (
                    TryGetNamedArgument(
                        masked,
                        argument,
                        out string argumentName,
                        out TextSpan value
                    ) && IsCallbackArgumentName(argumentName)
                )
                {
                    yield return value;
                    yield break;
                }
            }

            int callbackIndex = CallbackArgumentIndex(invocation.Name);
            if (0 <= callbackIndex && callbackIndex < arguments.Count)
            {
                yield return GetArgumentValueSpan(masked, arguments[callbackIndex]);
            }
        }

        private static int CallbackArgumentIndex(string registrationName)
        {
            if (MutableRegistrationNames.Contains(registrationName))
            {
                return 0;
            }
            if (
                registrationName == "RegisterUntargeted"
                || registrationName == "RegisterUntargetedPostProcessor"
                || registrationName.Contains("WithoutSource")
                || registrationName.Contains("WithoutTargeting")
            )
            {
                return 0;
            }
            return 1;
        }

        private static void AddCallbackReplacements(
            string masked,
            SourceMetadata metadata,
            TextSpan callback,
            HashSet<int> replacementStarts,
            List<(string Name, TextSpan Use)> methodGroups,
            SortedSet<string> manualReview
        )
        {
            Invocation expression = new(
                "callback",
                callback.Start,
                callback.Start + callback.Length
            );
            bool inline = TryGetInlineDelegateParameters(
                masked,
                expression,
                out TextSpan parameters
            );
            bool named = TryGetMethodGroup(
                masked,
                callback,
                out string methodName,
                out int nameStart
            );
            if (inline || named)
            {
                if (!inline && CallbackNameMaySelectValue(masked, metadata, methodName, nameStart))
                {
                    return;
                }
                int prefixEnd = inline ? parameters.Start - 1 : nameStart;
                int explicitDelegate = FindFastDelegateName(masked, callback.Start, prefixEnd);
                if (
                    0 <= explicitDelegate
                    && (
                        !TryClassifyFastDelegateType(
                            masked,
                            explicitDelegate,
                            metadata,
                            out bool proven,
                            out _
                        ) || !proven
                    )
                )
                {
                    manualReview.Add(
                        $"{CallbackLocation(masked, explicitDelegate)} uses a delegate whose MessageHandler type cannot be proven; inspect its binding manually"
                    );
                }
                else if (inline)
                {
                    AddRefReplacements(
                        masked,
                        parameters.Start,
                        parameters.Start + parameters.Length,
                        replacementStarts
                    );
                }
                else
                {
                    methodGroups.Add((methodName, new TextSpan(nameStart, methodName.Length)));
                }
                return;
            }
            if (TryGetQualifiedMethodGroup(masked, callback, out string qualifiedMethod))
            {
                manualReview.Add(
                    $"{CallbackLocation(masked, callback.Start)} uses {qualifiedMethod} as a qualified callback; inspect its declaration manually"
                );
                return;
            }
            string trimmed = masked.Substring(callback.Start, callback.Length).Trim();
            if (
                RefRegex.IsMatch(trimmed)
                || MethodGroupMayNeedMigration(masked, callback, masked, metadata)
                || (
                    IdentifierUseRegex.Match(trimmed) is { Success: true } identifier
                    && identifier.Length == trimmed.Length
                    && IdentifierValue(trimmed) != trimmed
                )
            )
            {
                manualReview.Add(
                    $"{CallbackLocation(masked, callback.Start)} uses callback syntax whose binding cannot be proven; inspect it manually"
                );
            }
        }

        private static bool TryGetMethodGroup(
            string masked,
            TextSpan argument,
            out string methodName,
            out int nameStart
        )
        {
            string value = masked.Substring(argument.Start, argument.Length).Trim();
            if (value is "null" or "default")
            {
                methodName = string.Empty;
                nameStart = -1;
                return false;
            }
            int valueStart = NextNonWhitespace(masked, argument.Start);
            Match match = NamedIdentifierRegex.Match(value);
            if (
                !match.Success
                && ConstructedHandlerRegex.IsMatch(NormalizeIdentifierSpellings(value))
            )
            {
                int closing = PreviousNonWhitespace(masked, argument.Start + argument.Length - 1);
                int nameEnd = PreviousNonWhitespace(masked, closing - 1);
                nameStart = FindIdentifierStart(masked, nameEnd);
                if (nameStart < 0)
                {
                    methodName = string.Empty;
                    return false;
                }
                methodName = IdentifierValue(masked.Substring(nameStart, nameEnd - nameStart + 1));
                return true;
            }
            if (!match.Success && 2 <= value.Length && value[0] == '(' && value[^1] == ')')
            {
                valueStart = NextNonWhitespace(masked, valueStart + 1);
                match = IdentifierRegex.Match(value.Substring(1, value.Length - 2).Trim());
            }
            Group name = match.Groups["name"];
            methodName = name.Value;
            nameStart = match.Success ? valueStart + name.Index : -1;
            return match.Success;
        }

        private static bool TryGetQualifiedMethodGroup(
            string masked,
            TextSpan argument,
            out string expression
        )
        {
            expression = masked.Substring(argument.Start, argument.Length).Trim();
            return Regex.IsMatch(
                    expression,
                    @"^(?:[A-Za-z_]\w*\s*\.\s*)+[A-Za-z_]\w*$",
                    RegexOptions.CultureInvariant
                ) && !Regex.IsMatch(expression, @"^this\s*\.", RegexOptions.CultureInvariant);
        }

        private static bool IsCallbackArgumentName(string argumentName)
        {
            return argumentName.EndsWith("Handler", StringComparison.OrdinalIgnoreCase)
                || argumentName.EndsWith("PostProcessor", StringComparison.OrdinalIgnoreCase)
                || argumentName.EndsWith("Interceptor", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetNamedArgument(
            string masked,
            TextSpan argument,
            out string argumentName,
            out TextSpan value
        )
        {
            string text = masked.Substring(argument.Start, argument.Length);
            Match match = Regex.Match(
                text,
                @"^\s*(?<name>" + IdentifierSpellingPattern + @")\s*:(?!:)\s*",
                RegexOptions.CultureInvariant
            );
            if (!match.Success)
            {
                argumentName = string.Empty;
                value = argument;
                return false;
            }

            argumentName = IdentifierValue(match.Groups["name"].Value);
            int valueStart = argument.Start + match.Length;
            value = new TextSpan(valueStart, argument.Start + argument.Length - valueStart);
            return true;
        }

        private static TextSpan GetArgumentValueSpan(string masked, TextSpan argument)
        {
            return TryGetNamedArgument(masked, argument, out _, out TextSpan value)
                ? value
                : argument;
        }

        private static bool TryGetInlineDelegateParameters(
            string masked,
            Invocation invocation,
            out TextSpan parameters
        )
        {
            parameters = default;
            int start = NextNonWhitespace(masked, invocation.Start);
            if (StartsWithIdentifier(masked, start, "static"))
            {
                start = NextNonWhitespace(masked, start + "static".Length);
            }
            if (StartsWithIdentifier(masked, start, "delegate"))
            {
                int open = NextNonWhitespace(masked, start + "delegate".Length);
                if (open < 0 || invocation.End <= open || masked[open] != '(')
                {
                    return false;
                }
                int close = FindMatching(masked, open, '(', ')');
                if (0 <= close && close < invocation.End)
                {
                    parameters = new TextSpan(open + 1, close - open - 1);
                    return true;
                }
                return false;
            }

            int arrow = masked.IndexOf(
                "=>",
                invocation.Start,
                invocation.End - invocation.Start,
                StringComparison.Ordinal
            );
            if (0 <= arrow)
            {
                int parameterEnd = PreviousNonWhitespace(masked, arrow - 1);
                if (invocation.Start <= parameterEnd && masked[parameterEnd] == ')')
                {
                    int parameterStart = FindMatchingBackward(masked, parameterEnd, '(', ')');
                    if (
                        invocation.Start <= parameterStart
                        && IsDirectDelegatePrefix(masked, invocation.Start, parameterStart)
                    )
                    {
                        parameters = new TextSpan(
                            parameterStart + 1,
                            parameterEnd - parameterStart - 1
                        );
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsSelfContainedCallbackArgument(string masked, TextSpan argument)
        {
            int start = NextNonWhitespace(masked, argument.Start);
            int end = PreviousNonWhitespace(masked, argument.Start + argument.Length - 1) + 1;
            while (
                0 <= start
                && start < end
                && masked[start] == '('
                && FindMatching(masked, start, '(', ')') == end - 1
            )
            {
                start = NextNonWhitespace(masked, start + 1);
                end = PreviousNonWhitespace(masked, end - 2) + 1;
            }
            if (start < 0 || end <= start)
            {
                return false;
            }

            if (
                masked.Substring(start, end - start) is "null" or "default"
                || TryGetInlineDelegateParameters(
                    masked,
                    new Invocation("callback", start, end),
                    out _
                )
                || HasSingleParameterLambda(masked, start, end)
            )
            {
                return true;
            }

            if (StartsWithIdentifier(masked, start, "static"))
            {
                start = NextNonWhitespace(masked, start + "static".Length);
            }
            if (!StartsWithIdentifier(masked, start, "delegate"))
            {
                return false;
            }
            int body = NextNonWhitespace(masked, start + "delegate".Length);
            return 0 <= body && body < end && masked[body] == '{';
        }

        private static bool HasSingleParameterLambda(string masked, int start, int end)
        {
            while (0 <= start && start < end)
            {
                Match parameter = DirectMemberIdentifierRegex.Match(masked, start, end - start);
                if (!parameter.Success)
                {
                    return false;
                }
                int after = NextNonWhitespace(masked, start + parameter.Length);
                if (0 <= after && after + 1 < end && IsArrow(masked, after))
                {
                    return true;
                }
                if (parameter.Value is not ("static" or "async"))
                {
                    return false;
                }
                start = after;
            }
            return false;
        }

        private static string NormalizeIdentifierSpellings(string text) =>
            IdentifierUseRegex.Replace(text, match => IdentifierValue(match.Value));

        private static bool IsDirectDelegatePrefix(string text, int start, int parameterStart)
        {
            string prefix = NormalizeIdentifierSpellings(
                text.Substring(start, parameterStart - start).Trim()
            );
            if (prefix.Length == 0 || prefix == "static")
            {
                return true;
            }
            return Regex.IsMatch(
                prefix,
                @"^(?:static\s+)?(?:\(\s*|new\s+)?(?:(?:global::)?DxMessaging\s*\.\s*Core\s*\.\s*)?MessageHandler\s*\.\s*FastHandler(?:WithContext)?\s*<[^()]+>\s*(?:\)\s*|\(\s*)$",
                RegexOptions.CultureInvariant
            );
        }

        private static void AddRefReplacements(
            string text,
            int start,
            int end,
            HashSet<int> replacementStarts
        )
        {
            foreach (TextSpan parameter in SplitParameters(text, start, end))
            {
                int modifier = FindRefParameterModifier(text, parameter);
                if (0 <= modifier)
                {
                    replacementStarts.Add(modifier);
                }
            }
        }

        private static bool HasRefParameter(string text, int start, int end) =>
            SplitParameters(text, start, end)
                .Exists(parameter => 0 <= FindRefParameterModifier(text, parameter));

        private static int FindRefParameterModifier(string text, TextSpan parameter)
        {
            int end = parameter.Start + parameter.Length;
            int start = NextNonWhitespace(text, parameter.Start);
            while (0 <= start && start < end)
            {
                if (text[start] == '[')
                {
                    int close = FindMatching(text, start, '[', ']');
                    if (close < 0 || end <= close)
                    {
                        break;
                    }
                    start = NextNonWhitespace(text, close + 1);
                    continue;
                }

                Match token = IdentifierUseRegex.Match(text, start, end - start);
                if (!token.Success || token.Index != start)
                {
                    break;
                }
                if (token.Value == "ref")
                {
                    return start;
                }
                if (token.Value is not ("this" or "scoped"))
                {
                    break;
                }
                start = NextNonWhitespace(text, start + token.Length);
            }
            return -1;
        }

        private static void AddBaseOverrideForwardingReplacements(
            string source,
            string masked,
            SourceMetadata metadata,
            ParameterList declaration,
            string methodName,
            HashSet<int> replacementStarts,
            Dictionary<int, int> baseForwardingOwners
        )
        {
            if (!TryFindCallableBody(masked, declaration.ParametersEnd, out TextSpan body))
            {
                return;
            }

            HashSet<string> refParameterNames = GetRefParameterNames(
                masked,
                declaration.ParametersStart,
                declaration.ParametersEnd
            );
            if (refParameterNames.Count == 0)
            {
                return;
            }

            List<TextSpan> overrideParameters = SplitParameters(
                masked,
                declaration.ParametersStart,
                declaration.ParametersEnd
            );
            bool scopesAreKnown = TryCollectLocalFunctionShadows(
                masked,
                body,
                metadata,
                out var localShadows
            );
            int owner = declaration.ParametersStart - 1;
            foreach (
                (int callStart, string member, List<TextSpan> arguments) in FindBaseCallArguments(
                    masked,
                    body
                )
            )
            {
                // The two contextual handlers take the same StringMessage parameter.
                bool samePayload =
                    member == methodName
                    || (methodName, member)
                        is
                            ("HandleStringGameObjectMessage", "HandleStringComponentMessage")
                            or
                            ("HandleStringComponentMessage", "HandleStringGameObjectMessage");
                for (int argumentIndex = 0; argumentIndex < arguments.Count; argumentIndex++)
                {
                    TextSpan argument = GetArgumentValueSpan(masked, arguments[argumentIndex]);
                    int refStart = FindBaseArgumentRefStart(masked, argument);
                    if (refStart < 0)
                    {
                        continue;
                    }

                    // A withheld base-argument edit also defers every callback that contains it.
                    baseForwardingOwners[refStart] = owner;
                    Match forwarded = BaseRefArgumentRegex.Match(
                        masked.Substring(argument.Start, argument.Length)
                    );
                    if (
                        scopesAreKnown
                        && samePayload
                        && arguments.Count == 1
                        && forwarded.Success
                        && argument.Start == arguments[argumentIndex].Start
                        && argumentIndex < overrideParameters.Count
                        && CanRewriteBaseArgument(
                            masked,
                            callStart,
                            overrideParameters[argumentIndex],
                            localShadows,
                            IdentifierValue(forwarded.Groups["name"].Value),
                            refParameterNames,
                            replacementStarts
                        )
                    )
                    {
                        replacementStarts.Add(refStart);
                    }
                }
            }

            int bodyEnd = body.Start + body.Length;
            int first = FindFirstInterpolationAtOrAfter(
                metadata.InterpolatedExpressions,
                body.Start
            );
            for (int index = first; index < metadata.InterpolatedExpressions.Count; index++)
            {
                TextSpan expression = metadata.InterpolatedExpressions[index];
                if (bodyEnd <= expression.Start)
                {
                    break;
                }
                if (bodyEnd < expression.Start + expression.Length)
                {
                    continue;
                }

                string code = MaskNonCode(
                    source.Substring(expression.Start, expression.Length),
                    null
                );
                TextSpan scope = new(0, code.Length);
                foreach ((_, _, List<TextSpan> arguments) in FindBaseCallArguments(code, scope))
                {
                    foreach (TextSpan argument in arguments)
                    {
                        int refStart = FindBaseArgumentRefStart(
                            code,
                            GetArgumentValueSpan(code, argument)
                        );
                        if (0 <= refStart)
                        {
                            // The hole may contain a nested callable with a different binding.
                            baseForwardingOwners[expression.Start + refStart] = owner;
                        }
                    }
                }
            }
        }

        private static IEnumerable<(
            int CallStart,
            string Member,
            List<TextSpan> Arguments
        )> FindBaseCallArguments(string code, TextSpan scope)
        {
            int scopeEnd = scope.Start + scope.Length;
            foreach (Match match in BaseCallRegex.Matches(code, scope.Start))
            {
                if (scopeEnd <= match.Index)
                {
                    break;
                }
                string member = IdentifierValue(match.Groups["member"].Value);
                if (!ChangedOverrideNames.Contains(member))
                {
                    continue;
                }
                int open = match.Index + match.Length - 1;
                int close = FindMatching(code, open, '(', ')');
                if (0 <= close && close < scopeEnd)
                {
                    yield return (match.Index, member, SplitArguments(code, open + 1, close));
                }
            }
        }

        private static int FindBaseArgumentRefStart(string code, TextSpan argument)
        {
            int refStart = NextNonWhitespace(code, argument.Start);
            return
                refStart < argument.Start + argument.Length
                && StartsWithIdentifier(code, refStart, "ref")
                ? refStart
                : -1;
        }

        private static bool CanRewriteBaseArgument(
            string masked,
            int callStart,
            TextSpan overrideParameter,
            List<(string Parameter, TextSpan Declaration)> localShadows,
            string argumentName,
            HashSet<string> overrideParameterNames,
            HashSet<int> replacementStarts
        )
        {
            TextSpan local = localShadows
                .LastOrDefault(shadow =>
                    shadow.Declaration.Start < callStart
                    && callStart < shadow.Declaration.Start + shadow.Declaration.Length
                )
                .Declaration;
            bool existingNameMatch = overrideParameterNames.Contains(argumentName);
            if (local.Length == 0)
            {
                return existingNameMatch;
            }

            int close = FindMatching(masked, local.Start, '(', ')');
            if (close < 0 || !TryFindCallableBody(masked, close, out TextSpan localBody))
            {
                return false;
            }
            if (!existingNameMatch && !IsDirectBaseCallWithinLocal(masked, localBody, callStart))
            {
                return false;
            }

            string expectedType = GetDeclaredRefType(masked, overrideParameter);
            HashSet<int> requiredReplacements = existingNameMatch ? null : replacementStarts;
            return expectedType != null
                && SplitParameters(masked, local.Start + 1, close)
                    .Any(parameter =>
                        GetDeclaredRefType(masked, parameter, argumentName, requiredReplacements)
                        == expectedType
                    );
        }

        private static string GetDeclaredRefType(
            string masked,
            TextSpan parameter,
            string name = null,
            HashSet<int> requiredReplacements = null
        )
        {
            Match declared = LocalFunctionParameterRegex.Match(
                masked,
                parameter.Start,
                parameter.Length
            );
            return
                declared.Success
                && declared.Groups["modifiers"].Value.Trim() == "ref"
                && (name == null || IdentifierValue(declared.Groups["name"].Value) == name)
                && requiredReplacements?.Contains(declared.Groups["modifiers"].Index) != false
                ? declared.Groups["type"].Value
                : null;
        }

        private static bool IsDirectBaseCallWithinLocal(string masked, TextSpan body, int callStart)
        {
            if (callStart < body.Start || body.Start + body.Length <= callStart)
            {
                return false;
            }
            int preceding = PreviousNonWhitespace(masked, callStart - 1);
            if (body.Start <= preceding && masked[preceding] is not (';' or '}'))
            {
                return false;
            }
            // A renamed argument is proved only at the callable's own statement level.
            // A deeper expression or block might belong to an unrecognized nested callable.
            int depth = 0;
            for (int index = body.Start; index < callStart; index++)
            {
                switch (masked[index])
                {
                    case '(' or '[' or '{':
                        depth++;
                        break;
                    case ')' or ']' or '}':
                        depth--;
                        break;
                }
            }
            return depth == 0;
        }

        private static void PruneIncompatibleParameterUses(
            string source,
            string masked,
            SourceMetadata metadata,
            List<Invocation> readonlyInvocations,
            HashSet<int> replacementStarts,
            Dictionary<int, int> baseForwardingOwners,
            SortedSet<string> manualReview
        )
        {
            Dictionary<int, List<int>> parameterReplacementsByOpen = new();
            foreach (int replacementStart in replacementStarts)
            {
                if (baseForwardingOwners.ContainsKey(replacementStart))
                {
                    continue;
                }
                int open = FindEnclosingOpenParenthesis(masked, replacementStart);
                if (open < 0)
                {
                    continue;
                }
                int close = FindMatching(masked, open, '(', ')');
                if (close < replacementStart)
                {
                    continue;
                }
                if (!parameterReplacementsByOpen.TryGetValue(open, out List<int> replacements))
                {
                    replacements = new List<int>();
                    parameterReplacementsByOpen.Add(open, replacements);
                }
                replacements.Add(replacementStart);
            }

            int[] delegateInitializers = RecordFastDelegateInitializerUses(masked, metadata);
            var globalRegistrations = readonlyInvocations
                .Where(invocation => invocation.Name == "RegisterGlobalAcceptAll")
                .Select(invocation => (Call: invocation, BindingIsProven: true))
                .Concat(
                    metadata.UnprovenGlobalRegistrations.Select(invocation =>
                        (Call: invocation, BindingIsProven: false)
                    )
                )
                .Select(registration =>
                {
                    TextSpan[] arguments = GetCallbackArguments(masked, registration.Call)
                        .ToArray();
                    int[] callbacks = arguments
                        .Select(callback => FindCallbackParametersOpen(masked, metadata, callback))
                        .ToArray();
                    bool mayUseSeparateInitializer = false;
                    for (int index = 0; index < callbacks.Length; index++)
                    {
                        TextSpan argument = arguments[index];
                        int open = callbacks[index];
                        if (
                            (open < argument.Start || argument.Start + argument.Length <= open)
                            && !IsSelfContainedCallbackArgument(masked, argument)
                        )
                        {
                            mayUseSeparateInitializer = true;
                            break;
                        }
                    }

                    return (
                        registration.Call.Start,
                        Callbacks: callbacks,
                        registration.BindingIsProven,
                        MayUseSeparateInitializer: mayUseSeparateInitializer
                    );
                })
                .ToArray();

            // Removing one edit can invalidate shared base arguments and entire registrations.
            bool recheck;
            do
            {
                recheck = false;
                foreach (
                    KeyValuePair<int, List<int>> entry in parameterReplacementsByOpen.Where(entry =>
                        replacementStarts.Contains(entry.Value[0])
                    )
                )
                {
                    recheck |= PruneConditionalCallback(
                        masked,
                        metadata.UncertainBranches,
                        replacementStarts,
                        baseForwardingOwners,
                        manualReview,
                        entry
                    )
                        ?? PruneCallback(
                            source,
                            masked,
                            metadata,
                            replacementStarts,
                            baseForwardingOwners,
                            manualReview,
                            entry
                        );
                }
                foreach (
                    (
                        int start,
                        int[] callbacks,
                        bool bindingIsProven,
                        bool mayUseSeparateInitializer
                    ) in globalRegistrations
                )
                {
                    bool HasPendingEdit(int open) =>
                        parameterReplacementsByOpen.TryGetValue(open, out List<int> edits)
                        && replacementStarts.Contains(edits[0]);

                    if (bindingIsProven && callbacks.All(HasPendingEdit))
                    {
                        continue;
                    }

                    // A tentative method lookup can turn out to point away from a delegate value.
                    // Once any sibling is withheld, preserve every initializer the call may use.
                    IEnumerable<int> coupled = mayUseSeparateInitializer
                        ? callbacks.Concat(delegateInitializers).Distinct()
                        : callbacks;
                    foreach (int open in coupled.Where(HasPendingEdit))
                    {
                        recheck |= metadata.NamedCallbackUseIssues.TryAdd(
                            open,
                            $"shares the RegisterGlobalAcceptAll call at {SourceLocation(masked, start)} with a callback that requires manual review"
                        );
                    }
                }
            } while (recheck);
        }

        private static int[] RecordFastDelegateInitializerUses(
            string masked,
            SourceMetadata metadata
        )
        {
            if (metadata.FastDelegateInitializers.Count == 0)
            {
                return Array.Empty<int>();
            }

            HashSet<int> initializers = new();
            string externalGlobalCall = metadata.FindExternalCallbackUse?.Invoke(
                "RegisterGlobalAcceptAll"
            );
            string interpolatedGlobalCall = FindInterpolatedGlobalRegistration(masked, metadata);
            foreach (var initializer in metadata.FastDelegateInitializers)
            {
                int variable = initializer.Variable;
                int open = FindCallbackParametersOpen(masked, metadata, initializer.Callback);
                if (open < 0)
                {
                    continue;
                }
                initializers.Add(open);

                if (interpolatedGlobalCall != null)
                {
                    metadata.NamedCallbackUseIssues.TryAdd(
                        open,
                        $"initializes a fast delegate that may feed the global registration at {interpolatedGlobalCall}, whose interpolated binding requires manual review"
                    );
                    continue;
                }
                string name = IdentifierValue(
                    DirectMemberIdentifierRegex.Match(masked, variable).Value
                );
                int type = FindContainingTypeStart(masked, variable);
                int block = FindEnclosingBrace(masked, variable);
                bool member = 0 <= type && block == type;
                bool mayHaveExternalUses =
                    member
                    && MemberMayHaveExternalUses(masked, type, initializer.TypeName, metadata);
                string issue =
                    metadata.FindExternalCallbackUse == null
                        ? null
                        : FindExternalFastDelegateUse(
                            masked,
                            metadata,
                            variable,
                            name,
                            externalGlobalCall,
                            mayHaveExternalUses
                        );
                issue ??= FindFastDelegateUseIssue(
                    masked,
                    metadata,
                    variable,
                    name,
                    (initializer.MessageType, initializer.HasContext),
                    (block, member, mayHaveExternalUses)
                );
                if (issue != null)
                {
                    metadata.NamedCallbackUseIssues.TryAdd(open, issue);
                }
            }
            return initializers.ToArray();
        }

        private static string FindInterpolatedGlobalRegistration(
            string masked,
            SourceMetadata metadata
        )
        {
            foreach (
                (string code, Match name, int offset) in metadata.IdentifierUses(
                    masked,
                    "RegisterGlobalAcceptAll"
                )
            )
            {
                if (offset == 0)
                {
                    continue;
                }
                int next = NextNonWhitespace(code, name.Index + name.Length);
                if (0 <= next && code[next] is '(' or '<')
                {
                    return SourceLocation(masked, name.Index + offset);
                }
            }
            return null;
        }

        private static string FindFastDelegateUseIssue(
            string masked,
            SourceMetadata metadata,
            int declaration,
            string name,
            (string MessageType, bool HasContext) storedDelegate,
            (int Block, bool IsMember, bool MayHaveExternalUses) scope
        )
        {
            int blockEnd = scope.Block < 0 ? -1 : FindMatching(masked, scope.Block, '{', '}');
            if (blockEnd < 0)
            {
                blockEnd = masked.Length;
            }
            foreach ((string code, Match use, int offset) in metadata.IdentifierUses(masked, name))
            {
                int position = use.Index + offset;
                int prefix = PreviousNonWhitespace(code, use.Index - 1);
                bool qualified = 0 <= prefix && code[prefix] == '.';
                bool mayBindToDelegate = scope.IsMember
                    ? scope.MayHaveExternalUses
                        || qualified
                        || (scope.Block < position && position < blockEnd)
                    : !qualified && declaration < position && position < blockEnd;
                if (position == declaration || !mayBindToDelegate)
                {
                    continue;
                }

                if (
                    offset == 0
                    && IsDirectFastDelegateRegistrationUse(
                        masked,
                        metadata,
                        position,
                        storedDelegate
                    )
                )
                {
                    continue;
                }

                if (
                    offset == 0
                    && scope.IsMember
                    && metadata.CanInspectProjectMemberUses
                    && TryFindFastDelegatePropertyAlias(
                        masked,
                        metadata,
                        use,
                        storedDelegate,
                        out int aliasPosition,
                        out string alias
                    )
                )
                {
                    var sameFileUse = metadata
                        .IdentifierUses(masked, alias)
                        .FirstOrDefault(candidate =>
                            candidate.Name.Index + candidate.Offset != aliasPosition
                        );
                    string location =
                        sameFileUse.Name == null
                            ? metadata.FindExternalCallbackUse(alias)
                            : SourceLocation(masked, sameFileUse.Name.Index + sameFileUse.Offset);
                    if (location != null)
                    {
                        return $"initializes fast delegate '{name}' with a use through property '{alias}' at {location} that cannot be proven safe for 'in'";
                    }
                    if (
                        MemberMayHaveExternalUses(
                            masked,
                            FindContainingTypeStart(masked, aliasPosition),
                            aliasPosition,
                            metadata
                        )
                    )
                    {
                        string possibleUse = metadata.FindPossibleExternalMemberUse(alias);
                        if (possibleUse != null)
                        {
                            return $"initializes fast delegate '{name}' with a possible use through property '{alias}' in {possibleUse} that cannot be proven safe for 'in'";
                        }
                    }
                    string lookup = metadata.FindPossibleMemberLookup(masked)
                        ?? FindPossibleAliasOwnerUse(masked, metadata, position);
                    if (lookup != null)
                    {
                        return $"initializes fast delegate '{name}' exposed as '{alias}' with a possible indirect use at {lookup}; verify its binding manually";
                    }
                    continue;
                }

                return $"initializes fast delegate '{name}' with a use on {SourceLocation(masked, position)} that cannot be proven safe for 'in'";
            }
            return null;
        }

        private static bool TryFindFastDelegatePropertyAlias(
            string masked,
            SourceMetadata metadata,
            Match use,
            (string MessageType, bool HasContext) storedDelegate,
            out int aliasPosition,
            out string alias
        )
        {
            aliasPosition = -1;
            alias = null;
            int arrowEnd = PreviousNonWhitespace(masked, use.Index - 1);
            int next = NextNonWhitespace(masked, use.Index + use.Length);
            int block = FindEnclosingBrace(masked, use.Index);
            if (
                arrowEnd < 1
                || !IsArrow(masked, arrowEnd - 1)
                || next < 0
                || masked[next] != ';'
                || block < 0
                || FindContainingTypeStart(masked, use.Index) != block
            )
            {
                return false;
            }

            int aliasEnd = PreviousNonWhitespace(masked, arrowEnd - 2);
            int name = FindIdentifierStart(masked, aliasEnd);
            if (name < 0)
            {
                return false;
            }

            int close = PreviousNonWhitespace(masked, name - 1);
            if (0 <= close && masked[close] == '?')
            {
                close = PreviousNonWhitespace(masked, close - 1);
            }
            if (close < 0 || masked[close] != '>')
            {
                return false;
            }
            int open = FindMatchingBackward(masked, close, '<', '>');
            if (
                open < 0
                || CompactType(masked.Substring(open + 1, close - open - 1))
                    != storedDelegate.MessageType
            )
            {
                return false;
            }

            int typeEnd = PreviousNonWhitespace(masked, open - 1);
            int typeStart = FindIdentifierStart(masked, typeEnd);
            if (
                typeStart < 0
                || !TryClassifyFastDelegateType(masked, typeStart, metadata, out bool proven, out _)
                || !proven
                || (
                    IdentifierValue(masked.Substring(typeStart, typeEnd - typeStart + 1))
                    == "FastHandlerWithContext"
                ) != storedDelegate.HasContext
            )
            {
                return false;
            }

            int declarationStart =
                masked.LastIndexOfAny(new[] { '{', '}', ';' }, typeStart - 1) + 1;
            if (
                metadata.UncertainBranches.Exists(branch =>
                    branch.Start <= next && declarationStart < branch.Start + branch.Length
                )
            )
            {
                return false;
            }

            aliasPosition = name;
            alias = IdentifierValue(masked.Substring(name, aliasEnd - name + 1));
            return true;
        }

        private static string FindPossibleAliasOwnerUse(
            string masked,
            SourceMetadata metadata,
            int propertyUse
        )
        {
            int owner = FindContainingTypeStart(masked, propertyUse);
            foreach (Match declaration in TypeDeclarationRegex.Matches(masked))
            {
                if (masked.IndexOf('{', declaration.Index + declaration.Length) != owner)
                {
                    continue;
                }

                Group identifier = declaration.Groups["name"];
                string name = IdentifierValue(identifier.Value);
                var local = metadata
                    .IdentifierUses(masked, name)
                    .FirstOrDefault(use => use.Name.Index + use.Offset != identifier.Index);
                return local.Name == null
                    ? metadata.FindExternalCallbackUse(name)
                    : SourceLocation(masked, local.Name.Index + local.Offset);
            }
            return SourceLocation(masked, propertyUse);
        }

        private static bool IsMemberLookupIdentifier(string name)
        {
            if (
                name
                is "Reflection"
                    or "BindingFlags"
                    or "MemberInfo"
                    or "PropertyInfo"
                    or "MethodInfo"
                    or "MethodBase"
                    or "TypeInfo"
                    or "Assembly"
                    or "Module"
                    or "AppDomain"
                    or "AssemblyLoadContext"
                    or "Expression"
                    or "TypeDescriptor"
                    or "TypeCache"
                    or "DynamicObject"
                    or "dynamic"
                    or "InvokeMember"
                    or "CreateDelegate"
                    or "DynamicInvoke"
            )
            {
                return true;
            }

            // Metadata can expose a property through either its name or its getter method.
            return name.EndsWith("ByName", StringComparison.Ordinal)
                || (
                    name.StartsWith("Get", StringComparison.Ordinal)
                    || name.StartsWith("Find", StringComparison.Ordinal)
                    || name.StartsWith("Lookup", StringComparison.Ordinal)
                    || name.StartsWith("Resolve", StringComparison.Ordinal)
                    || name.StartsWith("Bind", StringComparison.Ordinal)
                    || name.StartsWith("Declared", StringComparison.Ordinal)
                    || name.StartsWith("Exported", StringComparison.Ordinal)
                    || name.StartsWith("Defined", StringComparison.Ordinal)
                )
                    && (
                        name.Contains("Propert", StringComparison.Ordinal)
                        || name.Contains("Member", StringComparison.Ordinal)
                        || name.Contains("Method", StringComparison.Ordinal)
                        || name.Contains("Accessor", StringComparison.Ordinal)
                        || name.Contains("Assembly", StringComparison.Ordinal)
                        || name.Contains("Assemblies", StringComparison.Ordinal)
                        || name.Contains("Type", StringComparison.Ordinal)
                    );
        }

        private static bool IsDirectFastDelegateRegistrationUse(
            string masked,
            SourceMetadata metadata,
            int use,
            (string MessageType, bool HasContext) storedDelegate
        )
        {
            for (
                int open = FindEnclosingOpenParenthesis(masked, use);
                0 <= open;
                open = FindEnclosingOpenParenthesis(masked, open)
            )
            {
                int nameEnd = PreviousNonWhitespace(masked, open - 1);
                if (0 <= nameEnd && masked[nameEnd] == '>')
                {
                    int generic = FindMatchingBackward(masked, nameEnd, '<', '>');
                    nameEnd = PreviousNonWhitespace(masked, generic - 1);
                }
                int nameStart = FindIdentifierStart(masked, nameEnd);
                if (nameStart < 0)
                {
                    continue;
                }
                Match method = DirectMemberIdentifierRegex.Match(masked, nameStart);
                string name = IdentifierValue(method.Value);
                if (name == "RegisterGlobalAcceptAll" || !ReadonlyRegistrationNames.Contains(name))
                {
                    continue;
                }

                return TryReadRegistrationInvocation(
                        masked,
                        method,
                        name,
                        out Invocation invocation,
                        out List<TextSpan> typeArguments
                    )
                    && GetCallbackArguments(masked, invocation)
                        .Any(argument =>
                            TryGetMethodGroup(masked, argument, out _, out int position)
                            && position == use
                        )
                    && HasProvenTokenReceiver(masked, nameStart, metadata)
                    && MatchesPackageRegistration(
                        masked,
                        metadata,
                        invocation,
                        typeArguments,
                        storedDelegate
                    )
                    && !metadata.HasCompetingRegistrationExtension(masked, invocation);
            }
            return false;
        }

        private static string FindExternalFastDelegateUse(
            string masked,
            SourceMetadata metadata,
            int declaration,
            string name,
            string externalGlobalCall,
            bool mayHaveExternalUses
        )
        {
            if (mayHaveExternalUses)
            {
                string external = metadata.FindPossibleExternalMemberUse(name);
                if (external != null)
                {
                    return $"initializes fast delegate '{name}' with a possible use in {external} that cannot be proven safe for 'in'";
                }
            }
            if (
                externalGlobalCall != null
                && metadata
                    .IdentifierUses(masked, name)
                    .Any(use => use.Name.Index + use.Offset != declaration)
            )
            {
                return $"initializes fast delegate '{name}' whose value could reach the global registration in {externalGlobalCall}; its binding cannot be proven safe for 'in'";
            }
            return null;
        }

        private static bool CallbackNameMaySelectValue(
            string masked,
            SourceMetadata metadata,
            string name,
            int use
        )
        {
            int type = FindContainingTypeStart(masked, use);
            foreach (
                (Match declaration, int position) in metadata.ReceiverDeclarations(masked, name)
            )
            {
                if (position == use || FindContainingTypeStart(masked, position) != type)
                {
                    continue;
                }

                Match reference = DirectMemberIdentifierRegex.Match(masked, position);
                int suffix = NextNonWhitespace(masked, position + reference.Length);
                string possibleType = declaration.Groups["type"].Value;
                if (
                    suffix < 0
                    || masked[suffix] is '(' or '<'
                    || possibleType is "return" or "throw" or "yield" or "case" or "goto" or "await"
                )
                {
                    continue;
                }
                if (IsReceiverDeclarationVisible(masked, declaration, position, type, use))
                {
                    return true;
                }
            }
            return false;
        }

        private static int FindCallbackParametersOpen(
            string masked,
            SourceMetadata metadata,
            TextSpan callback
        )
        {
            Invocation expression = new(
                "callback",
                callback.Start,
                callback.Start + callback.Length
            );
            if (TryGetInlineDelegateParameters(masked, expression, out TextSpan parameters))
            {
                return parameters.Start - 1;
            }
            if (
                !TryGetMethodGroup(masked, callback, out string name, out int use)
                || CallbackNameMaySelectValue(masked, metadata, name, use)
            )
            {
                return -1;
            }
            int typeScope = FindContainingTypeStart(masked, use);
            List<ParameterList> declarations = FindMethodDeclarations(masked, name, false)
                .FindAll(declaration =>
                    FindContainingTypeStart(masked, declaration.DeclarationStart) == typeScope
                );
            if (declarations.Count != 1)
            {
                return -1;
            }

            ParameterList selected = declarations[0];
            int declarationBlock = FindEnclosingBrace(masked, selected.DeclarationStart);
            if (declarationBlock != typeScope)
            {
                int close =
                    declarationBlock < 0 ? -1 : FindMatching(masked, declarationBlock, '{', '}');
                int prefix = PreviousNonWhitespace(masked, use - 1);
                if (
                    use <= declarationBlock
                    || close <= use
                    || (0 <= prefix && masked[prefix] == '.')
                )
                {
                    return -1;
                }
            }
            return selected.ParametersStart - 1;
        }

        private static bool? PruneConditionalCallback(
            string masked,
            List<TextSpan> uncertainBranches,
            HashSet<int> replacementStarts,
            Dictionary<int, int> baseForwardingOwners,
            SortedSet<string> manualReview,
            KeyValuePair<int, List<int>> entry
        )
        {
            if (uncertainBranches.Count == 0)
            {
                return null;
            }

            int close = FindMatching(masked, entry.Key, '(', ')');
            TextSpan body = default;
            bool hasBody = 0 <= close && TryFindCallableBody(masked, close, out body);
            int end = hasBody ? body.Start + body.Length : masked.Length;
            if (
                !uncertainBranches.Exists(branch =>
                    entry.Key < branch.Start + branch.Length && branch.Start <= end
                )
            )
            {
                return null;
            }

            replacementStarts.ExceptWith(entry.Value);
            int remainingSignatureEdits = replacementStarts.Count;
            replacementStarts.ExceptWith(
                baseForwardingOwners
                    .Where(forwarding =>
                        forwarding.Value == entry.Key
                        || (
                            body.Start <= forwarding.Key
                            && forwarding.Key < body.Start + body.Length
                        )
                    )
                    .Select(forwarding => forwarding.Key)
            );
            manualReview.Add(
                $"{CallbackLocation(masked, entry.Key)} has boundaries that depend on conditional compilation; inspect it manually"
            );
            return replacementStarts.Count < remainingSignatureEdits;
        }

        private static bool PruneCallback(
            string source,
            string masked,
            SourceMetadata metadata,
            HashSet<int> replacementStarts,
            Dictionary<int, int> baseForwardingOwners,
            SortedSet<string> manualReview,
            KeyValuePair<int, List<int>> entry
        )
        {
            int close = FindMatching(masked, entry.Key, '(', ')');
            if (close < 0 || !TryFindCallableBody(masked, close, out TextSpan body))
            {
                replacementStarts.ExceptWith(entry.Value);
                int remainingEdits = replacementStarts.Count;
                replacementStarts.ExceptWith(
                    baseForwardingOwners
                        .Where(forwarding => forwarding.Value == entry.Key)
                        .Select(forwarding => forwarding.Key)
                );
                manualReview.Add(
                    $"{CallbackLocation(masked, entry.Key)} has a body whose boundaries cannot be established; inspect it manually"
                );
                return replacementStarts.Count < remainingEdits;
            }

            HashSet<string> parameterNames = new(StringComparer.Ordinal);
            bool hasUnsupportedParameter = false;
            foreach (int replacementStart in entry.Value)
            {
                string parameterName = FindParameterName(
                    masked,
                    entry.Key + 1,
                    close,
                    replacementStart
                );
                if (0 < parameterName.Length)
                {
                    parameterNames.Add(parameterName);
                }
                else
                {
                    hasUnsupportedParameter = true;
                }
            }

            bool scopesAreKnown = TryCollectLocalFunctionShadows(
                masked,
                body,
                metadata,
                out var localShadows
            );

            List<int> forwardingStarts = new();
            int enclosingOverride = -1;
            string reason = scopesAreKnown
                ? null
                : "has local function scopes that cannot be established";
            int bodyEnd = body.Start + body.Length;
            foreach (
                KeyValuePair<int, int> forwarding in baseForwardingOwners.Where(forwarding =>
                    body.Start <= forwarding.Key && forwarding.Key < bodyEnd
                )
            )
            {
                forwardingStarts.Add(forwarding.Key);
                if (forwarding.Value < entry.Key)
                {
                    enclosingOverride = forwarding.Value;
                }
                if (!replacementStarts.Contains(forwarding.Key))
                {
                    reason = "depends on a base forwarding argument that requires manual review";
                }
            }

            foreach (string parameterName in parameterNames)
            {
                string parameterValue = IdentifierValue(parameterName);
                Match unpaired = FindByReferenceUses(masked, body, parameterValue)
                    .LastOrDefault(match =>
                        !replacementStarts.Contains(match.Index)
                        && !baseForwardingOwners.ContainsKey(match.Index)
                        && !IsLocalFunctionShadow(localShadows, parameterValue, match.Index)
                    );
                if (unpaired != null)
                {
                    reason =
                        $"passes parameter '{parameterName}' by {unpaired.Groups["modifier"].Value}";
                }
            }

            reason ??= FindParameterAttributeIssue(
                masked,
                metadata,
                entry.Key + 1,
                close,
                entry.Value
            );
            if (reason == null && hasUnsupportedParameter)
            {
                reason = "has unsupported parameter syntax";
            }
            if (
                reason == null
                && 0 <= enclosingOverride
                && HasDirectLocalFunctionCall(
                    source,
                    masked,
                    metadata.InterpolatedExpressions,
                    enclosingOverride,
                    entry.Key
                )
            )
            {
                reason = "has a direct local function call that depends on its 'ref' parameter";
            }
            if (reason == null)
            {
                metadata.NamedCallbackUseIssues.TryGetValue(entry.Key, out reason);
            }
            reason ??= FindParameterUseIssue(
                source,
                masked,
                body,
                metadata.InterpolatedExpressions,
                parameterNames,
                localShadows
            );
            if (reason == null)
            {
                return false;
            }
            replacementStarts.ExceptWith(entry.Value);
            int remainingSignatureEdits = replacementStarts.Count;
            replacementStarts.ExceptWith(forwardingStarts);
            manualReview.Add(
                $"{CallbackLocation(masked, entry.Key)} {reason}; inspect it manually"
            );
            return replacementStarts.Count < remainingSignatureEdits;
        }

        private static string FindParameterAttributeIssue(
            string masked,
            SourceMetadata metadata,
            int parametersStart,
            int parametersEnd,
            List<int> replacements
        )
        {
            foreach (TextSpan parameter in SplitParameters(masked, parametersStart, parametersEnd))
            {
                int modifier = FindRefParameterModifier(masked, parameter);
                if (!replacements.Contains(modifier))
                {
                    continue;
                }

                string prefix = masked.Substring(parameter.Start, modifier - parameter.Start);
                foreach (Match token in IdentifierUseRegex.Matches(prefix))
                {
                    string name = IdentifierValue(token.Value);
                    if (
                        name is "Out" or "OutAttribute"
                        || metadata.MayAliasType(masked, name)
                        || metadata.MayAliasType(masked, name + "Attribute")
                    )
                    {
                        return $"has an attribute at {SourceLocation(masked, parameter.Start + token.Index)} that may require writable 'ref'";
                    }
                }
            }
            return null;
        }

        private static bool HasDirectLocalFunctionCall(
            string source,
            string masked,
            List<TextSpan> interpolatedExpressions,
            int enclosingOverride,
            int localFunctionParameters
        )
        {
            int close = FindMatching(masked, enclosingOverride, '(', ')');
            if (
                close < 0
                || !TryFindCallableBody(masked, close, out TextSpan scope)
                || localFunctionParameters < scope.Start
            )
            {
                return false;
            }

            int nameEnd = PreviousNonWhitespace(masked, localFunctionParameters - 1);
            string localName = null;
            for (
                Match identifier = IdentifierUseRegex.Match(
                    masked,
                    scope.Start,
                    localFunctionParameters - scope.Start
                );
                identifier.Success;
                identifier = identifier.NextMatch()
            )
            {
                if (identifier.Index + identifier.Length - 1 == nameEnd)
                {
                    localName = IdentifierValue(identifier.Value);
                    break;
                }
            }
            if (localName == null)
            {
                return false;
            }

            if (ContainsDirectCall(masked, scope, localName, localFunctionParameters))
            {
                return true;
            }
            int scopeEnd = scope.Start + scope.Length;
            int first = FindFirstInterpolationAtOrAfter(interpolatedExpressions, scope.Start);
            for (int index = first; index < interpolatedExpressions.Count; index++)
            {
                TextSpan expression = interpolatedExpressions[index];
                if (scopeEnd <= expression.Start)
                {
                    break;
                }
                if (expression.Start + expression.Length <= scopeEnd)
                {
                    string code = MaskNonCode(
                        source.Substring(expression.Start, expression.Length),
                        null
                    );
                    if (ContainsDirectCall(code, new TextSpan(0, code.Length), localName, -1))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool ContainsDirectCall(
            string code,
            TextSpan scope,
            string localName,
            int declarationParameterStart
        )
        {
            int scopeEnd = scope.Start + scope.Length;
            for (
                Match identifier = IdentifierUseRegex.Match(code, scope.Start, scope.Length);
                identifier.Success;
                identifier = identifier.NextMatch()
            )
            {
                if (IdentifierValue(identifier.Value) != localName)
                {
                    continue;
                }
                int following = NextNonWhitespace(code, identifier.Index + identifier.Length);
                if (
                    0 <= following
                    && following < scopeEnd
                    && following != declarationParameterStart
                    && code[following] == '('
                )
                {
                    int preceding = PreviousNonWhitespace(code, identifier.Index - 1);
                    if (preceding < scope.Start || code[preceding] != '.')
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TryCollectLocalFunctionShadows(
            string masked,
            TextSpan body,
            SourceMetadata metadata,
            out List<(string Parameter, TextSpan Declaration)> shadows
        )
        {
            // Conditional compilation can change the declaration or the end of its scope.
            if (
                metadata.UncertainBranches.Exists(branch =>
                    branch.Start < body.Start + body.Length
                    && body.Start < branch.Start + branch.Length
                )
            )
            {
                shadows = new();
                return false;
            }
            try
            {
                shadows = FindLocalFunctionShadows(masked, body).ToList();
                return true;
            }
            catch (RegexMatchTimeoutException exception)
            {
                DxMessagingEditorLog.LogWarning(
                    "Local function scope detection timed out.",
                    exception
                );
                shadows = new();
                return false;
            }
        }

        private static IEnumerable<(
            string Parameter,
            TextSpan Declaration
        )> FindLocalFunctionShadows(string masked, TextSpan body)
        {
            int bodyEnd = body.Start + body.Length;
            for (
                Match function = LocalFunctionDeclarationRegex.Match(
                    masked,
                    body.Start,
                    body.Length
                );
                function.Success;
                function = function.NextMatch()
            )
            {
                int before = PreviousNonWhitespace(masked, function.Index - 1);
                if (
                    StartsWithIdentifier(masked, function.Index, "new")
                    || (body.Start <= before && "{;}:".IndexOf(masked[before]) < 0)
                )
                {
                    continue;
                }

                int open = function.Index + function.Length - 1;
                int close = FindMatching(masked, open, '(', ')');
                int next = close < 0 ? -1 : NextNonWhitespace(masked, close + 1);
                if (next < 0 || bodyEnd <= next)
                {
                    continue;
                }

                int end = masked[next] switch
                {
                    '{' => FindMatching(masked, next, '{', '}'),
                    '=' when next + 1 < bodyEnd && masked[next + 1] == '>' => FindStatementEnd(
                        masked,
                        next + 2
                    ),
                    _ => -1,
                };
                if (end < 0 || bodyEnd <= end)
                {
                    continue;
                }

                foreach (TextSpan parameter in SplitParameters(masked, open + 1, close))
                {
                    Match declared = LocalFunctionParameterRegex.Match(
                        masked,
                        parameter.Start,
                        parameter.Length
                    );
                    if (declared.Success)
                    {
                        yield return (
                            IdentifierValue(declared.Groups["name"].Value),
                            new TextSpan(open, end - open)
                        );
                    }
                }
            }
        }

        private static bool IsLocalFunctionShadow(
            List<(string Parameter, TextSpan Declaration)> shadows,
            string parameterValue,
            int start,
            int length = 1
        ) =>
            shadows?.Exists(shadow =>
                shadow.Parameter == parameterValue
                && shadow.Declaration.Start <= start
                && start + length <= shadow.Declaration.Start + shadow.Declaration.Length
            ) == true;

        private static string FindParameterUseIssue(
            string source,
            string masked,
            TextSpan body,
            List<TextSpan> interpolatedExpressions,
            HashSet<string> parameterNames,
            List<(string Parameter, TextSpan Declaration)> localShadows
        )
        {
            // Record unresolved reads while continuing to look for established incompatibilities.
            string unverifiedRead = null;
            foreach (string parameterName in parameterNames)
            {
                string parameterValue = IdentifierValue(parameterName);
                string issue = FindDirectParameterIssue(
                    masked,
                    body,
                    parameterName,
                    parameterValue,
                    ref unverifiedRead,
                    localShadows
                )
                    ?? FindInterpolatedParameterIssue(
                        source,
                        body,
                        interpolatedExpressions,
                        parameterName,
                        parameterValue,
                        ref unverifiedRead,
                        localShadows
                    );
                if (issue != null)
                {
                    return issue;
                }
            }
            return unverifiedRead;
        }

        private static string IdentifierValue(string spelling)
        {
            string value = spelling.TrimStart('@');
            if (0 <= value.IndexOf('\\'))
            {
                value = UnicodeEscapeRegex.Replace(value, match =>
                    int.TryParse(
                        match.Value.Substring(2),
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out int scalar
                    )
                    && 0 <= scalar
                    && scalar <= 0x10FFFF
                    && (scalar < 0xD800 || 0xDFFF < scalar)
                        ? char.ConvertFromUtf32(scalar)
                        : match.Value
                );
            }
            return IdentifierFormattingRegex.Replace(value, string.Empty);
        }

        private static IEnumerable<Match> FindByReferenceUses(
            string source,
            TextSpan scope,
            string parameterValue
        )
        {
            int scopeEnd = scope.Start + scope.Length;
            for (
                Match use = ByReferenceModifierRegex.Match(source, scope.Start, scope.Length);
                use.Success;
                use = use.NextMatch()
            )
            {
                int start = NextAfterComments(source, use.Index + use.Length, scopeEnd);
                if (start < 0 || scopeEnd <= start)
                {
                    continue;
                }

                Match operand = ByReferenceOperandRegex.Match(source, start, scopeEnd - start);
                if (
                    operand.Success
                    && IdentifierValue(operand.Groups["name"].Value) == parameterValue
                    && HasValidReferenceGrouping(source, scope, use, operand)
                    && !IsReadonlyRefLocalInitializer(source, scope, use, operand)
                )
                {
                    yield return use;
                }
            }
        }

        private static bool IsReadonlyRefLocalInitializer(
            string source,
            TextSpan scope,
            Match use,
            Match operand
        )
        {
            if (use.Groups["modifier"].Value != "ref")
            {
                return false;
            }

            ReadOnlySpan<char> prefix = source.AsSpan(scope.Start, use.Index - scope.Start);
            int declarationStart = scope.Start + prefix.LastIndexOfAny(';', '{', '}') + 1;
            Match declaration = ReadonlyRefLocalPrefixRegex.Match(
                source,
                declarationStart,
                use.Index - declarationStart
            );
            if (!declaration.Success || declaration.Index + declaration.Length != use.Index)
            {
                return false;
            }

            int scopeEnd = scope.Start + scope.Length;
            TextSpan rightHandSide = new(operand.Index, scopeEnd - operand.Index);
            int targetStart = operand.Groups["name"].Index;
            int following = NextAfterComments(source, operand.Index + operand.Length, scopeEnd);
            ExpandDirectReceiver(source, rightHandSide, ref targetStart, ref following);
            return targetStart == rightHandSide.Start
                && 0 <= following
                && following < scopeEnd
                && source[following] == ';';
        }

        private static bool HasValidReferenceGrouping(
            string source,
            TextSpan scope,
            Match modifier,
            Match operand
        )
        {
            Group grouping = operand.Groups["grouping"];
            if (grouping.Length == 0)
            {
                return true;
            }

            int scopeEnd = scope.Start + scope.Length;
            TextSpan operandScope = new(grouping.Index, scopeEnd - grouping.Index);
            Group name = operand.Groups["name"];
            int targetStart = name.Index;
            int following = NextAfterComments(source, name.Index + name.Length, scopeEnd);
            if (
                ReadDirectTarget(source, operandScope, ref targetStart, ref following, out _)
                    != DirectTargetKind.Storage
                || targetStart != operandScope.Start
            )
            {
                return false;
            }

            if (following < 0 || scopeEnd <= following || ",);]".IndexOf(source[following]) >= 0)
            {
                return true;
            }

            // A grouped first operand also ends at the colon of a ref conditional.
            int beforeModifier = PreviousNonWhitespace(source, modifier.Index - 1);
            int alternate = NextAfterComments(source, following + 1, scopeEnd);
            return source[following] == ':'
                && modifier.Groups["modifier"].Value == "ref"
                && scope.Start <= beforeModifier
                && source[beforeModifier] == '?'
                && 0 <= alternate
                && alternate + "ref".Length <= scopeEnd
                && StartsWithIdentifier(source, alternate, "ref");
        }

        private static int NextAfterComments(string source, int start, int limit)
        {
            int next = NextNonWhitespace(source, start);
            while (0 <= next && next + 1 < limit && source[next] == '/')
            {
                int end = FindCommentEnd(source, next, limit);
                if (end < 0)
                {
                    break;
                }
                next = NextNonWhitespace(source, end);
            }
            return next;
        }

        private static int FindFirstInterpolationAtOrAfter(List<TextSpan> expressions, int start)
        {
            int first = 0;
            int end = expressions.Count;
            while (first < end)
            {
                int middle = first + ((end - first) / 2);
                if (expressions[middle].Start < start)
                {
                    first = middle + 1;
                }
                else
                {
                    end = middle;
                }
            }
            return first;
        }

        private static string FindInterpolatedParameterIssue(
            string source,
            TextSpan body,
            List<TextSpan> interpolatedExpressions,
            string parameterName,
            string parameterValue,
            ref string unverifiedRead,
            List<(string Parameter, TextSpan Declaration)> localShadows
        )
        {
            int bodyEnd = body.Start + body.Length;
            int first = FindFirstInterpolationAtOrAfter(interpolatedExpressions, body.Start);
            for (int index = first; index < interpolatedExpressions.Count; index++)
            {
                TextSpan scope = interpolatedExpressions[index];
                if (bodyEnd <= scope.Start)
                {
                    break;
                }
                if (bodyEnd < scope.Start + scope.Length)
                {
                    continue;
                }
                if (IsLocalFunctionShadow(localShadows, parameterValue, scope.Start, scope.Length))
                {
                    continue;
                }

                string code = MaskNonCode(source.Substring(scope.Start, scope.Length), null);
                TextSpan maskedScope = new(0, code.Length);
                Match forwarding = FindByReferenceUses(code, maskedScope, parameterValue)
                    .FirstOrDefault();
                if (forwarding != null)
                {
                    return $"passes parameter '{parameterName}' by {forwarding.Groups["modifier"].Value}";
                }
                string issue = FindDirectParameterIssue(
                    code,
                    maskedScope,
                    parameterName,
                    parameterValue,
                    ref unverifiedRead
                );
                if (issue != null)
                {
                    return issue;
                }
            }
            return null;
        }

        private static string FindDirectParameterIssue(
            string masked,
            TextSpan body,
            string parameterName,
            string parameterValue,
            ref string unverifiedRead,
            List<(string Parameter, TextSpan Declaration)> localShadows = null
        )
        {
            int bodyEnd = body.Start + body.Length;
            for (
                Match use = IdentifierUseRegex.Match(masked, body.Start, body.Length);
                use.Success;
                use = use.NextMatch()
            )
            {
                if (
                    IdentifierValue(use.Value) != parameterValue
                    || IsLocalFunctionShadow(localShadows, parameterValue, use.Index, use.Length)
                )
                {
                    continue;
                }

                int previous = PreviousNonWhitespace(masked, use.Index - 1);
                if (
                    body.Start <= previous
                    && masked[previous] == '.'
                    && !FollowsRangeOperator(masked, body.Start, previous)
                )
                {
                    continue;
                }

                int targetStart = use.Index;
                int next = NextAfterComments(masked, use.Index + use.Length, bodyEnd);
                DirectTargetKind target = ReadDirectTarget(
                    masked,
                    body,
                    ref targetStart,
                    ref next,
                    out bool accessedMemberOrIndexer
                );
                if (
                    target == DirectTargetKind.Storage
                    && IsAddressTakenInFixedInitializer(masked, body, targetStart, next)
                )
                {
                    return $"takes the address of parameter '{parameterName}' or its member in a fixed initializer";
                }
                if (
                    target == DirectTargetKind.MemberInvocation
                    || (
                        target == DirectTargetKind.Storage
                        && !accessedMemberOrIndexer
                        && (
                            IsDirectForeachSource(masked, body, targetStart, next)
                            || IsDirectQuerySource(masked, body, targetStart, next)
                            || IsDirectFixedInitializer(masked, body, targetStart, next)
                        )
                    )
                )
                {
                    return $"invokes a method on parameter '{parameterName}' or its member";
                }
                if (
                    target == DirectTargetKind.Storage
                    && IsDirectTypedReferenceOperand(masked, body, targetStart, next)
                )
                {
                    return $"takes a writable reference to parameter '{parameterName}' or its member with __makeref";
                }
                if (
                    target == DirectTargetKind.Storage
                    && IsDirectParameterWrite(masked, body, targetStart, next)
                )
                {
                    return $"writes to parameter '{parameterName}' or its member";
                }
                if (accessedMemberOrIndexer)
                {
                    unverifiedRead ??=
                        $"reads a member or indexer on parameter '{parameterName}' that cannot be verified safe for 'in'";
                }
            }
            return null;
        }

        private static bool IsDirectTypedReferenceOperand(
            string text,
            TextSpan body,
            int targetStart,
            int following
        )
        {
            if (following < 0 || body.Start + body.Length <= following || text[following] != ')')
            {
                return false;
            }

            int opening = PreviousNonWhitespace(text, targetStart - 1);
            return body.Start <= opening
                && text[opening] == '('
                && FollowsKeyword(
                    text,
                    body.Start,
                    PreviousNonWhitespace(text, opening - 1),
                    "__makeref"
                );
        }

        private static bool IsDirectForeachSource(
            string text,
            TextSpan body,
            int targetStart,
            int following
        )
        {
            if (following < 0 || body.Start + body.Length <= following || text[following] != ')')
            {
                return false;
            }

            int beforeTarget = PreviousNonWhitespace(text, targetStart - 1);
            if (!FollowsKeyword(text, body.Start, beforeTarget, "in"))
            {
                return false;
            }

            // An ordinary argument can also follow in and precede a closing parenthesis.
            int opening = FindMatchingBackward(text, following, '(', ')', body.Start);
            int beforeOpening = PreviousNonWhitespace(text, opening - 1);
            return FollowsKeyword(text, body.Start, beforeOpening, "foreach");
        }

        private static bool IsAddressTakenInFixedInitializer(
            string text,
            TextSpan body,
            int targetStart,
            int following
        )
        {
            int address = PreviousNonWhitespace(text, targetStart - 1);
            if (address < body.Start || text[address] != '&')
            {
                return false;
            }
            ExpandStandaloneGrouping(text, body, ref address, ref following);
            return IsDirectFixedInitializer(text, body, address, following);
        }

        private static bool IsDirectFixedInitializer(
            string text,
            TextSpan body,
            int targetStart,
            int following
        )
        {
            if (
                following < 0
                || body.Start + body.Length <= following
                || text[following] is not (')' or ',')
            )
            {
                return false;
            }

            int equals = PreviousNonWhitespace(text, targetStart - 1);
            if (equals < body.Start || text[equals] != '=')
            {
                return false;
            }

            int opening = FindEnclosingOpenParenthesis(text, targetStart);
            return body.Start <= opening
                && FollowsKeyword(
                    text,
                    body.Start,
                    PreviousNonWhitespace(text, opening - 1),
                    "fixed"
                );
        }

        private static bool IsDirectQuerySource(
            string text,
            TextSpan body,
            int targetStart,
            int following
        )
        {
            int bodyEnd = body.Start + body.Length;
            while (0 <= following && following < bodyEnd && text[following] == '!')
            {
                following = NextAfterComments(text, following + 1, bodyEnd);
            }
            int beforeTarget = PreviousNonWhitespace(text, targetStart - 1);
            if (
                following < 0
                || bodyEnd <= following
                || !FollowsKeyword(text, body.Start, beforeTarget, "in")
            )
            {
                return false;
            }

            if (StartsWithIdentifier(text, following, "on"))
            {
                // A typed join calls Cast<T>() on its source before passing it to Join.
                int rangeVariableEnd = PreviousNonWhitespace(text, beforeTarget - "in".Length);
                int rangeVariable = FindIdentifierStart(text, rangeVariableEnd);
                return body.Start <= rangeVariable
                    && !FollowsKeyword(
                        text,
                        body.Start,
                        PreviousNonWhitespace(text, rangeVariable - 1),
                        "join"
                    );
            }
            return StartsWithIdentifier(text, following, "select")
                || StartsWithIdentifier(text, following, "where")
                || StartsWithIdentifier(text, following, "from")
                || StartsWithIdentifier(text, following, "join")
                || StartsWithIdentifier(text, following, "let")
                || StartsWithIdentifier(text, following, "orderby")
                || StartsWithIdentifier(text, following, "group");
        }

        private static bool IsDirectParameterWrite(
            string text,
            TextSpan body,
            int targetStart,
            int following
        )
        {
            int bodyEnd = body.Start + body.Length;
            if (0 <= following && following < bodyEnd)
            {
                Match write = DirectWriteOperatorRegex.Match(text, following);
                if (write.Success && following + write.Length <= bodyEnd)
                {
                    return true;
                }

                if (
                    text[following] is ',' or ')'
                    && IsDirectDeconstructionTarget(text, body, targetStart, following)
                )
                {
                    return true;
                }
            }
            int previous = PreviousNonWhitespace(text, targetStart - 1);
            return FollowsPrefixWriteOperator(text, body.Start, previous);
        }

        private enum DirectTargetKind
        {
            Unsupported,
            Storage,
            MemberInvocation,
        }

        private static DirectTargetKind ReadDirectTarget(
            string text,
            TextSpan body,
            ref int targetStart,
            ref int following,
            out bool accessedMemberOrIndexer
        )
        {
            int bodyEnd = body.Start + body.Length;
            accessedMemberOrIndexer = false;
            ExpandDirectReceiver(text, body, ref targetStart, ref following);
            bool followsMember = false;
            while (0 <= following && following < bodyEnd)
            {
                if (text[following] == '[')
                {
                    accessedMemberOrIndexer = true;
                    int indexerEnd = FindIndexerEnd(text, following, bodyEnd);
                    if (indexerEnd < 0)
                    {
                        return DirectTargetKind.Unsupported;
                    }

                    followsMember = false;
                    following = NextAfterComments(text, indexerEnd + 1, bodyEnd);
                }
                else if (text[following] == '.')
                {
                    if (following + 1 < bodyEnd && text[following + 1] == '.')
                    {
                        return DirectTargetKind.Storage;
                    }

                    accessedMemberOrIndexer = true;
                    int memberStart = NextAfterComments(text, following + 1, bodyEnd);
                    if (memberStart < 0 || bodyEnd <= memberStart)
                    {
                        return DirectTargetKind.Unsupported;
                    }

                    Match member = DirectMemberIdentifierRegex.Match(
                        text,
                        memberStart,
                        bodyEnd - memberStart
                    );
                    if (!member.Success)
                    {
                        return DirectTargetKind.Unsupported;
                    }

                    followsMember = true;
                    following = NextAfterComments(text, member.Index + member.Length, bodyEnd);
                }
                else
                {
                    break;
                }

                ExpandDirectReceiver(text, body, ref targetStart, ref following);
            }

            if (following < 0 || bodyEnd <= following)
            {
                return DirectTargetKind.Storage;
            }

            accessedMemberOrIndexer |= IsConditionalMemberAccess(text, following, bodyEnd);
            return text[following] switch
            {
                '(' when followsMember => DirectTargetKind.MemberInvocation,
                '.' or '[' or '(' => DirectTargetKind.Unsupported,
                _ => DirectTargetKind.Storage,
            };
        }

        private static int FindIndexerEnd(string text, int opening, int bodyEnd)
        {
            int depth = 1;
            for (int index = opening + 1; index < bodyEnd; index++)
            {
                if (text[index] == '[')
                {
                    depth++;
                }
                else if (text[index] == ']' && --depth == 0)
                {
                    return index;
                }
            }
            return -1;
        }

        private static bool IsConditionalMemberAccess(string text, int start, int bodyEnd)
        {
            if (text[start] != '?')
            {
                return false;
            }

            int access = NextAfterComments(text, start + 1, bodyEnd);
            return 0 <= access && access < bodyEnd && text[access] is '.' or '[';
        }

        private static void ExpandDirectReceiver(
            string text,
            TextSpan body,
            ref int targetStart,
            ref int following
        )
        {
            int bodyEnd = body.Start + body.Length;
            ExpandStandaloneGrouping(text, body, ref targetStart, ref following);
            while (0 <= following && following < bodyEnd && text[following] == '!')
            {
                int after = NextAfterComments(text, following + 1, bodyEnd);
                if (after < 0 || bodyEnd <= after)
                {
                    return;
                }

                bool postfixWrite =
                    after + 1 < bodyEnd
                    && (text[after] is '+' or '-')
                    && text[after] == text[after + 1];
                if (
                    text[after] is not ('.' or '[' or ')' or '?' or ',' or ':' or ';')
                    && !postfixWrite
                )
                {
                    return;
                }

                following = after;
                ExpandStandaloneGrouping(text, body, ref targetStart, ref following);
            }
        }

        private static void ExpandStandaloneGrouping(
            string text,
            TextSpan body,
            ref int targetStart,
            ref int following
        )
        {
            int bodyEnd = body.Start + body.Length;
            while (0 <= following && following < bodyEnd && text[following] == ')')
            {
                int opening = PreviousNonWhitespace(text, targetStart - 1);
                if (opening < body.Start || text[opening] != '(')
                {
                    return;
                }

                int beforeOpening = PreviousNonWhitespace(text, opening - 1);
                int groupingStart = opening;
                // Checked expressions preserve the classification of their operand.
                if (FollowsKeyword(text, body.Start, beforeOpening, "checked"))
                {
                    groupingStart = beforeOpening - "checked".Length + 1;
                }
                else if (FollowsKeyword(text, body.Start, beforeOpening, "unchecked"))
                {
                    groupingStart = beforeOpening - "unchecked".Length + 1;
                }

                // A preceding ! can be a prefix operator or null forgiveness on a callable.
                while (body.Start <= beforeOpening && text[beforeOpening] == '!')
                {
                    beforeOpening = PreviousNonWhitespace(text, beforeOpening - 1);
                }
                if (
                    groupingStart == opening
                    && !FollowsGroupingBoundary(text, body.Start, beforeOpening)
                    && !FollowsCast(text, body.Start, beforeOpening)
                )
                {
                    return;
                }

                targetStart = groupingStart;
                following = NextAfterComments(text, following + 1, bodyEnd);
            }
        }

        private static bool FollowsGroupingBoundary(string text, int bodyStart, int previous) =>
            FollowsTargetBoundary(text, bodyStart, previous)
            || "+-*/%&|^~<".Contains(text[previous])
            || FollowsRangeOperator(text, bodyStart, previous)
            || FollowsStandaloneGreaterThan(text, bodyStart, previous)
            || FollowsGroupingOperandKeyword(text, bodyStart, previous);

        private static bool FollowsCast(string text, int bodyStart, int previous)
        {
            // A type-shaped group at an operand boundary can precede another cast or grouping.
            while (bodyStart <= previous && text[previous] == ')')
            {
                int opening = FindMatchingBackward(text, previous, '(', ')', bodyStart);
                if (opening < bodyStart || !IsCastType(text, opening + 1, previous))
                {
                    return false;
                }

                previous = PreviousNonWhitespace(text, opening - 1);
                if (FollowsGroupingBoundary(text, bodyStart, previous))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsCastType(
            string text,
            int start,
            int end,
            bool allowElementName = false
        )
        {
            Match namedType = GroupedCastTypeRegex.Match(text, start, end - start);
            if (namedType.Success)
            {
                return allowElementName || !namedType.Groups["elementName"].Success;
            }

            int open = NextNonWhitespace(text, start);
            if (open < 0 || end <= open || text[open] != '(')
            {
                return false;
            }

            int close = FindMatching(text, open, '(', ')');
            if (close < 0 || end <= close)
            {
                return false;
            }

            Match suffix = GroupedCastTupleSuffixRegex.Match(text, close + 1, end - close - 1);
            if (!suffix.Success || (!allowElementName && suffix.Groups["elementName"].Success))
            {
                return false;
            }

            List<TextSpan> elements = SplitParameters(text, open + 1, close);
            return 1 < elements.Count
                && elements.All(element =>
                    IsCastType(
                        text,
                        element.Start,
                        element.Start + element.Length,
                        allowElementName: true
                    )
                );
        }

        private static bool FollowsGroupingOperandKeyword(string text, int bodyStart, int previous)
        {
            const string Guard = "when";
            return FollowsKeyword(text, bodyStart, previous, "throw")
                || FollowsKeyword(text, bodyStart, previous, "in")
                || (
                    FollowsKeyword(text, bodyStart, previous, Guard)
                    && FollowsSwitchPattern(text, bodyStart, previous - Guard.Length)
                );
        }

        private static bool FollowsKeyword(string text, int bodyStart, int previous, string keyword)
        {
            int keywordStart = previous - keyword.Length + 1;
            return bodyStart <= keywordStart
                && StartsWithIdentifier(text, keywordStart, keyword)
                && (
                    keywordStart == bodyStart
                    || (text[keywordStart - 1] != '@' && !IsIdentifierPart(text[keywordStart - 1]))
                );
        }

        private static bool FollowsSwitchPattern(string text, int bodyStart, int beforeWhen)
        {
            int index = PreviousNonWhitespace(text, beforeWhen);
            // An ordinary call to a method named when can occur inside a switch.
            if (index < bodyStart || "({[,;:=!?.&|^~+-*/%".IndexOf(text[index]) >= 0)
            {
                return false;
            }

            bool passedArmSeparator = false;
            for (; bodyStart <= index; index--)
            {
                switch (text[index])
                {
                    case ')' or ']' or '}':
                        index = FindPatternGroupStart(text, bodyStart, index);
                        break;
                    case ',':
                        passedArmSeparator = true;
                        break;
                    case '(' or '[' or ';':
                    case ':' when !passedArmSeparator:
                    case '>'
                        when !passedArmSeparator && bodyStart < index && text[index - 1] == '=':
                    case 'n'
                        when !passedArmSeparator && FollowsKeyword(text, bodyStart, index, "when"):
                        return false;
                    case '{':
                        int beforeBrace = PreviousNonWhitespace(text, index - 1);
                        return FollowsKeyword(text, bodyStart, beforeBrace, "switch");
                    case 'e'
                        when !passedArmSeparator && FollowsKeyword(text, bodyStart, index, "case"):
                        return true;
                }
            }
            return false;
        }

        private static int FindPatternGroupStart(string text, int bodyStart, int closing)
        {
            char close = text[closing];
            char open = close switch
            {
                ')' => '(',
                ']' => '[',
                _ => '{',
            };
            return FindMatchingBackward(text, closing, open, close, bodyStart);
        }

        private static bool FollowsRangeOperator(string text, int bodyStart, int previous) =>
            bodyStart < previous && text[previous - 1] == '.' && text[previous] == '.';

        private static bool FollowsStandaloneGreaterThan(
            string text,
            int bodyStart,
            int previous
        ) =>
            text[previous] == '>'
            && bodyStart < previous
            && text[previous - 1] != '='
            && !ClosesTypeArgumentList(text, bodyStart, previous);

        private static bool ClosesTypeArgumentList(string text, int bodyStart, int closing)
        {
            int angles = 1;
            for (int index = closing - 1; bodyStart <= index; index--)
            {
                char current = text[index];
                if (current == '>')
                {
                    angles++;
                }
                else if (current == '<')
                {
                    if (text[index + 1] == '<')
                    {
                        return false;
                    }
                    if (--angles == 0)
                    {
                        int nameEnd = PreviousNonWhitespace(text, index - 1);
                        return bodyStart <= nameEnd && IsIdentifierPart(text[nameEnd]);
                    }
                }
                else if (current == ')' || current == ']')
                {
                    // A tuple type or array rank can appear inside the argument list.
                    char opening = current == ')' ? '(' : '[';
                    int groupStart = FindMatchingBackward(text, index, opening, current, bodyStart);
                    if (
                        groupStart < 0
                        || !IsTypeArgumentGrouping(text, bodyStart, groupStart, index)
                    )
                    {
                        return false;
                    }
                    index = groupStart;
                }
                else if (
                    "([{;}=+-/%&|^~!".IndexOf(current) >= 0
                    || (
                        char.IsDigit(current)
                        && (index == bodyStart || !IsIdentifierPart(text[index - 1]))
                    )
                )
                {
                    // An unmatched opener, operator, or numeric literal separates these angles.
                    return false;
                }
            }
            return false;
        }

        private static bool IsTypeArgumentGrouping(
            string text,
            int bodyStart,
            int opening,
            int closing
        )
        {
            if (text[opening] == '(')
            {
                int before = PreviousNonWhitespace(text, opening - 1);
                return (before < bodyStart || text[before] is '<' or ',' or '(')
                    && 1 < SplitParameters(text, opening + 1, closing).Count;
            }

            for (int index = opening + 1; index < closing; index++)
            {
                if (text[index] != ',' && !char.IsWhiteSpace(text[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool FollowsPrefixWriteOperator(string text, int bodyStart, int previous)
        {
            if (previous <= bodyStart)
            {
                return false;
            }

            char preceding = text[previous];
            // In count+++message, the first two signs are the local's postfix operator.
            return (preceding == '+' || preceding == '-')
                && text[previous - 1] == preceding
                && (previous - 1 == bodyStart || text[previous - 2] != preceding);
        }

        private static bool IsDirectDeconstructionTarget(
            string text,
            TextSpan body,
            int targetStart,
            int targetDelimiter
        )
        {
            int bodyEnd = body.Start + body.Length;
            int targetEnd = PreviousNonWhitespace(text, targetDelimiter - 1);
            for (
                int tupleStart = FindEnclosingOpenParenthesis(text, targetStart);
                body.Start <= tupleStart;
                tupleStart = FindEnclosingOpenParenthesis(text, tupleStart)
            )
            {
                int beforeTuple = PreviousNonWhitespace(text, tupleStart - 1);
                if (!FollowsTargetBoundary(text, body.Start, beforeTuple))
                {
                    return false;
                }

                int tupleEnd = FindMatching(text, tupleStart, '(', ')');
                if (tupleEnd < 0 || bodyEnd <= tupleEnd)
                {
                    return false;
                }

                int afterTuple = NextNonWhitespace(text, tupleEnd + 1);
                if (afterTuple < 0 || bodyEnd <= afterTuple)
                {
                    return false;
                }

                char following = text[afterTuple];
                bool assigned =
                    following == '='
                    && DirectWriteOperatorRegex
                        .Match(text, afterTuple, bodyEnd - afterTuple)
                        .Success;
                if (!assigned && following != ',' && following != ')')
                {
                    return false;
                }

                List<TextSpan> elements = SplitArguments(text, tupleStart + 1, tupleEnd);
                if (elements.Count < 2)
                {
                    return false;
                }

                if (
                    !elements.Exists(element =>
                        NextNonWhitespace(text, element.Start) == targetStart
                        && PreviousNonWhitespace(text, element.Start + element.Length - 1)
                            == targetEnd
                    )
                )
                {
                    return false;
                }
                if (assigned)
                {
                    return true;
                }

                targetStart = tupleStart;
                targetEnd = tupleEnd;
            }
            return false;
        }

        private static bool FollowsTargetBoundary(string text, int bodyStart, int beforeTarget) =>
            beforeTarget < bodyStart
            || "({[};,:?=".IndexOf(text[beforeTarget]) >= 0
            || (
                bodyStart < beforeTarget
                && text[beforeTarget - 1] == '='
                && text[beforeTarget] == '>'
            )
            || FollowsTupleControlStatement(text, bodyStart, beforeTarget);

        private static bool FollowsTupleControlStatement(
            string text,
            int bodyStart,
            int beforeTuple
        )
        {
            bool followsCondition = text[beforeTuple] == ')';
            int keywordEnd = beforeTuple;
            if (followsCondition)
            {
                int conditionStart = FindMatchingBackward(text, beforeTuple, '(', ')');
                if (conditionStart < bodyStart)
                {
                    return false;
                }
                keywordEnd = PreviousNonWhitespace(text, conditionStart - 1);
            }
            if (keywordEnd < bodyStart)
            {
                return false;
            }

            // Leave one character before the longest keyword for the identifier boundary check.
            int start = Math.Max(bodyStart, keywordEnd - "foreach".Length);
            Match keyword = TupleControlKeywordRegex.Match(text, start, keywordEnd - start + 1);
            return keyword.Success && followsCondition == keyword.Groups["condition"].Success;
        }

        private static int FindEnclosingOpenParenthesis(string text, int position)
        {
            int depth = 0;
            for (int index = position - 1; 0 <= index; index--)
            {
                if (text[index] == ')')
                {
                    depth++;
                }
                else if (text[index] == '(')
                {
                    if (depth == 0)
                    {
                        return index;
                    }
                    depth--;
                }
            }
            return -1;
        }

        private static HashSet<string> GetRefParameterNames(string text, int start, int end)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (TextSpan parameter in SplitParameters(text, start, end))
            {
                if (FindRefParameterModifier(text, parameter) < 0)
                {
                    continue;
                }
                string parameterText = text.Substring(parameter.Start, parameter.Length);
                MatchCollection identifiers = IdentifierUseRegex.Matches(parameterText);
                if (0 < identifiers.Count)
                {
                    names.Add(IdentifierValue(identifiers[identifiers.Count - 1].Value));
                }
            }
            return names;
        }

        private static string FindParameterName(
            string text,
            int parametersStart,
            int parametersEnd,
            int modifierStart
        )
        {
            foreach (TextSpan parameter in SplitParameters(text, parametersStart, parametersEnd))
            {
                if (
                    modifierStart < parameter.Start
                    || parameter.Start + parameter.Length <= modifierStart
                )
                {
                    continue;
                }
                MatchCollection identifiers = IdentifierUseRegex.Matches(
                    text.Substring(parameter.Start, parameter.Length)
                );
                if (identifiers.Count == 0)
                {
                    return string.Empty;
                }
                Match identifier = identifiers[identifiers.Count - 1];
                string suffix = text.Substring(
                    parameter.Start + identifier.Index + identifier.Length,
                    parameter.Length - identifier.Index - identifier.Length
                );
                return string.IsNullOrWhiteSpace(suffix) ? identifier.Value : string.Empty;
            }
            return string.Empty;
        }

        private static bool TryFindCallableBody(string text, int parametersEnd, out TextSpan body)
        {
            int next = NextNonWhitespace(text, parametersEnd + 1);
            if (next < 0)
            {
                body = default;
                return false;
            }

            int blockStart = text[next] == '{' ? next : -1;
            int expressionStart =
                next + 1 < text.Length && text[next] == '=' && text[next + 1] == '>' ? next : -1;
            if (blockStart < 0 && expressionStart < 0 && StartsWithIdentifier(text, next, "where"))
            {
                int semicolon = text.IndexOf(';', next + "where".Length);
                blockStart = text.IndexOf('{', next + "where".Length);
                expressionStart = text.IndexOf(
                    "=>",
                    next + "where".Length,
                    StringComparison.Ordinal
                );
                if (0 <= semicolon)
                {
                    if (semicolon < blockStart)
                    {
                        blockStart = -1;
                    }
                    if (semicolon < expressionStart)
                    {
                        expressionStart = -1;
                    }
                }
            }
            if (0 <= blockStart && (expressionStart < 0 || blockStart < expressionStart))
            {
                int blockEnd = FindMatching(text, blockStart, '{', '}');
                if (0 <= blockEnd)
                {
                    body = new TextSpan(blockStart + 1, blockEnd - blockStart - 1);
                    return true;
                }
            }
            if (0 <= expressionStart)
            {
                int expressionEnd = FindExpressionEnd(text, expressionStart + 2);
                body = new TextSpan(expressionStart + 2, expressionEnd - expressionStart - 2);
                return true;
            }
            body = default;
            return false;
        }

        private static int FindExpressionEnd(string text, int start)
        {
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            int angles = 0;
            for (int index = start; index < text.Length; index++)
            {
                switch (text[index])
                {
                    case '(':
                        parentheses++;
                        break;
                    case ')':
                        if (parentheses == 0 && brackets == 0 && braces == 0)
                        {
                            return index;
                        }
                        parentheses--;
                        break;
                    case '[':
                        brackets++;
                        break;
                    case ']':
                        brackets--;
                        break;
                    case '{':
                        braces++;
                        break;
                    case '}':
                        if (parentheses == 0 && brackets == 0 && braces == 0)
                        {
                            return index;
                        }
                        braces--;
                        break;
                    case '<'
                        when parentheses == 0
                            && brackets == 0
                            && braces == 0
                            && (0 < angles || IsGenericOpenAngle(text, index, text.Length)):
                        angles++;
                        break;
                    case '>' when parentheses == 0 && brackets == 0 && braces == 0 && 0 < angles:
                        angles--;
                        break;
                    case ',':
                        if (parentheses == 0 && brackets == 0 && braces == 0 && angles == 0)
                        {
                            return index;
                        }
                        break;
                    case ';':
                        if (parentheses == 0 && brackets == 0 && braces == 0)
                        {
                            return index;
                        }
                        break;
                }
            }
            return text.Length;
        }

        private static bool IsGenericOpenAngle(
            string text,
            int openAngle,
            int end,
            bool allowClosingAngleFollower = false
        )
        {
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            int angles = 1;
            for (int index = openAngle + 1; index < end; index++)
            {
                switch (text[index])
                {
                    case '(':
                        parentheses++;
                        break;
                    case ')':
                        if (parentheses == 0 && brackets == 0 && braces == 0)
                        {
                            return false;
                        }
                        parentheses--;
                        break;
                    case '[':
                        brackets++;
                        break;
                    case ']':
                        brackets--;
                        break;
                    case '{':
                        braces++;
                        break;
                    case '}':
                        if (parentheses == 0 && brackets == 0 && braces == 0)
                        {
                            return false;
                        }
                        braces--;
                        break;
                    case '<' when parentheses == 0 && brackets == 0 && braces == 0:
                        angles++;
                        break;
                    case '>' when parentheses == 0 && brackets == 0 && braces == 0:
                        angles--;
                        if (angles == 0)
                        {
                            int next = NextNonWhitespace(text, index + 1);
                            if (
                                0 <= next
                                && !IsGenericAngleFollower(text[next])
                                && !(allowClosingAngleFollower && text[next] == '>')
                                && !IsGenericTypeTestOperand(text, openAngle)
                            )
                            {
                                return false;
                            }
                            return SplitParameters(text, openAngle + 1, index)
                                .TrueForAll(argument =>
                                    IsCastType(
                                        text,
                                        argument.Start,
                                        argument.Start + argument.Length
                                    )
                                );
                        }
                        break;
                    case '=' when parentheses == 0 && brackets == 0 && braces == 0:
                    case ';':
                        if (parentheses == 0 && brackets == 0 && braces == 0)
                        {
                            return false;
                        }
                        break;
                }
            }
            return false;
        }

        private static bool IsGenericAngleFollower(char value)
        {
            return value == '('
                || value == '.'
                || value == '['
                || value == '{'
                || value == '?'
                || value == '!'
                || value == ','
                || value == ';'
                || value == ')'
                || value == '}';
        }

        private static bool IsGenericTypeTestOperand(string text, int openAngle)
        {
            int end = PreviousNonWhitespace(text, openAngle - 1);
            int start = FindIdentifierStart(text, end);
            while (0 <= start)
            {
                int previous = PreviousNonWhitespace(text, start - 1);
                if (previous < 0)
                {
                    break;
                }
                if (text[previous] == '.')
                {
                    end = PreviousNonWhitespace(text, previous - 1);
                }
                else if (1 <= previous && text[previous] == ':' && text[previous - 1] == ':')
                {
                    end = PreviousNonWhitespace(text, previous - 2);
                }
                else
                {
                    if (FollowsKeyword(text, 0, previous, "not"))
                    {
                        previous = PreviousNonWhitespace(text, previous - "not".Length);
                    }
                    return FollowsKeyword(text, 0, previous, "is")
                        || FollowsKeyword(text, 0, previous, "as")
                        || FollowsKeyword(text, 0, previous, "and")
                        || FollowsKeyword(text, 0, previous, "or");
                }
                start = FindIdentifierStart(text, end);
            }
            return false;
        }

        private static string CallbackLocation(string text, int position) =>
            $"callback on {SourceLocation(text, position)}";

        private static string ConditionalDirectiveIssue(string source, int position) =>
            $"Script boundaries depend on conditional compilation at {SourceLocation(source, position)}. A directive can be hidden by a comment or string in some build configurations. Verify the callback bindings in each configuration.";

        private static string SourceLocation(string text, int position)
        {
            int line = 1;
            int lineStart = 0;
            for (int index = 0; index < position; index++)
            {
                char current = text[index];
                if (CSharpLineTerminators.Contains(current))
                {
                    if (current != '\n' || index == 0 || text[index - 1] != '\r')
                    {
                        line++;
                    }
                    lineStart = index + 1;
                }
            }
            return $"line {line}, column {position - lineStart + 1}";
        }

        private static List<TextSpan> SplitParameters(string text, int start, int end) =>
            SplitArguments(text, start, end, containsParameterTypes: true);

        private static List<TextSpan> SplitArguments(
            string text,
            int start,
            int end,
            bool containsParameterTypes = false
        )
        {
            List<TextSpan> arguments = new();
            int argumentStart = start;
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            int angles = 0;
            bool readingParameterType = containsParameterTypes;
            for (int index = start; index < end; index++)
            {
                switch (text[index])
                {
                    case '(':
                        parentheses++;
                        break;
                    case ')':
                        parentheses--;
                        break;
                    case '[':
                        brackets++;
                        break;
                    case ']':
                        brackets--;
                        break;
                    case '{':
                        braces++;
                        break;
                    case '}':
                        braces--;
                        break;
                    case '=' when parentheses == 0 && brackets == 0 && braces == 0 && angles == 0:
                        readingParameterType = false;
                        break;
                    // A comparison in a nested or default expression is not evidence of a type argument.
                    case '<'
                        when parentheses == 0
                            && brackets == 0
                            && braces == 0
                            && (
                                0 < angles
                                || readingParameterType
                                || IsGenericOpenAngle(text, index, end)
                            ):
                        angles++;
                        break;
                    case '>' when parentheses == 0 && brackets == 0 && braces == 0 && 0 < angles:
                        angles--;
                        break;
                    case ',':
                        if (parentheses == 0 && brackets == 0 && braces == 0 && angles == 0)
                        {
                            arguments.Add(new TextSpan(argumentStart, index - argumentStart));
                            argumentStart = index + 1;
                            readingParameterType = containsParameterTypes;
                        }
                        break;
                }
            }
            arguments.Add(new TextSpan(argumentStart, end - argumentStart));
            return arguments;
        }

        private static int FindStatementEnd(string text, int start)
        {
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            for (int index = start; index < text.Length; index++)
            {
                switch (text[index])
                {
                    // This delimiter belongs to the surrounding expression or block.
                    case ')' when parentheses == 0:
                    case ']' when brackets == 0:
                    case '}' when braces == 0:
                        return -1;
                    case '(':
                        parentheses++;
                        break;
                    case ')':
                        parentheses--;
                        break;
                    case '[':
                        brackets++;
                        break;
                    case ']':
                        brackets--;
                        break;
                    case '{':
                        braces++;
                        break;
                    case '}':
                        braces--;
                        break;
                    case ';':
                        if (parentheses == 0 && brackets == 0 && braces == 0)
                        {
                            return index;
                        }
                        break;
                }
            }
            return -1;
        }

        private static string MaskNonCode(
            string source,
            List<TextSpan> interpolatedExpressions,
            SourceMetadata metadata = null
        )
        {
            char[] masked = source.ToCharArray();
            ConditionalMask conditional =
                metadata != null && source.Contains('#') ? new(metadata) : null;
            for (int index = 0; index < source.Length; )
            {
                if (
                    conditional != null
                    && source[index] == '#'
                    && IsDirectiveStart(source.AsSpan(), index)
                )
                {
                    index = MaskDirective(source, masked, index, conditional);
                    continue;
                }

                if (conditional?.State == BranchState.Inactive)
                {
                    Mask(masked, index, index + 1);
                    index++;
                    continue;
                }

                int end = FindNonCodeEnd(source, index, interpolatedExpressions);
                if (index < end)
                {
                    if (conditional?.State == BranchState.Uncertain)
                    {
                        conditional.ObserveMaskedToken(source.AsSpan(index, end - index), index);
                    }
                    Mask(masked, index, end);
                    index = end;
                }
                else
                {
                    index++;
                }
            }
            conditional?.Complete(masked);
            return new string(masked);
        }

        private static int MaskDirective(
            string source,
            char[] masked,
            int index,
            ConditionalMask conditional
        )
        {
            ReadOnlySpan<char> text = source.AsSpan();
            int lineEnd = FindDirectiveLineEnd(text, index);
            string keyword = ReadConditionalKeyword(text, index, lineEnd, out int keywordEnd);
            if (keyword != null)
            {
                conditional.Visit(keyword, text.Slice(keywordEnd, lineEnd - keywordEnd), index);
            }
            Mask(masked, index, lineEnd);
            return lineEnd;
        }

        private static string ReadConditionalKeyword(
            ReadOnlySpan<char> text,
            int hash,
            int lineEnd,
            out int keywordEnd
        )
        {
            int keywordStart = hash + 1;
            while (keywordStart < lineEnd && char.IsWhiteSpace(text[keywordStart]))
            {
                keywordStart++;
            }
            keywordEnd = keywordStart;
            while (keywordEnd < lineEnd && IsIdentifierPart(text[keywordEnd]))
            {
                keywordEnd++;
            }

            string keyword = text.Slice(keywordStart, keywordEnd - keywordStart).ToString();
            return keyword is "if" or "elif" or "else" or "endif" ? keyword : null;
        }

        private static bool IsDirectiveStart(ReadOnlySpan<char> source, int index)
        {
            for (int prior = index - 1; 0 <= prior; prior--)
            {
                if (CSharpLineTerminators.Contains(source[prior]))
                {
                    return true;
                }
                if (!char.IsWhiteSpace(source[prior]))
                {
                    return false;
                }
            }
            return true;
        }

        private static int FindDirectiveLineEnd(ReadOnlySpan<char> source, int start)
        {
            int lineEnd = source.Slice(start).IndexOfAny(CSharpLineTerminators.AsSpan());
            return lineEnd < 0 ? source.Length : start + lineEnd;
        }

        private enum BranchState
        {
            Active,
            Inactive,
            Uncertain,
        }

        private sealed class ConditionalGroup
        {
            public ConditionalGroup(int start)
            {
                Start = start;
            }

            public int Start { get; }
            public List<TextSpan> Branches { get; } = new();
        }

        private sealed class SourceMetadata
        {
            public SourceMetadata(
                string source,
                Func<string, string> findExternalCallbackUse = null,
                ProjectCallbackUses projectTypes = null,
                string assemblyName = null,
                bool projectSourcesLimitedToAssets = false,
                Func<string, string> findPackageBindingIssue = null
            )
            {
                Source = source;
                FindExternalCallbackUse = findExternalCallbackUse;
                _projectTypes = projectTypes;
                _assemblyName = assemblyName;
                _projectSourcesLimitedToAssets = projectSourcesLimitedToAssets;
                _findPackageBindingIssue = findPackageBindingIssue;
            }

            private readonly bool _projectSourcesLimitedToAssets;
            private readonly Func<string, string> _findPackageBindingIssue;
            private readonly Dictionary<string, string> _packageBindingIssues = new(
                StringComparer.Ordinal
            );

            public readonly SortedSet<string> PackageBindingReviews = new(StringComparer.Ordinal);

            public string PackageBindingIssue(string type, int use = -1)
            {
                if (!type.StartsWith("DxMessaging.", StringComparison.Ordinal))
                {
                    return null;
                }
                if (!_packageBindingIssues.TryGetValue(type, out string issue))
                {
                    issue =
                        _findPackageBindingIssue != null
                            ? _findPackageBindingIssue(SimpleTypeName(type))
                        : _projectSourcesLimitedToAssets
                            ? "the installed package binding is unavailable for this compilation"
                        : null;
                    _packageBindingIssues.Add(type, issue);
                }
                if (issue != null && 0 <= use)
                {
                    PackageBindingReviews.Add(
                        $"{CallbackLocation(Source, use)} {type} needs manual review because {issue}"
                    );
                }
                return issue;
            }

            public string FindPossibleExternalMemberUse(string name) =>
                FindExternalCallbackUse?.Invoke(name)
                ?? (_projectSourcesLimitedToAssets ? "scripts outside Assets" : null);

            public string Source { get; }

            public Func<string, string> FindExternalCallbackUse { get; }

            public readonly List<TextSpan> InterpolatedExpressions = new();
            public readonly List<TextSpan> UncertainBranches = new();
            public readonly List<int> ConditionalDirectives = new();
            public int? UncertainDirectivePosition { get; set; }
            public readonly List<ConditionalGroup> CompleteConditionalGroups = new();
            public readonly Dictionary<int, string> NamedCallbackUseIssues = new();
            public readonly List<(
                int TypeName,
                int Variable,
                TextSpan Callback,
                string MessageType,
                bool HasContext
            )> FastDelegateInitializers = new();
            public readonly List<Invocation> UnprovenGlobalRegistrations = new();

            private readonly ProjectCallbackUses _projectTypes;
            private readonly string _assemblyName;
            private HashSet<(string Name, int Arity)> _qualifiedTypeDeclarations;
            private readonly List<(int Start, int End, string Path)> _typeBodies = new();
            private readonly HashSet<(string Name, int Arity)> _uncertainTypeDeclarations = new();
            public readonly HashSet<(string Name, int Arity)> DuplicateTypeDeclarations = new();
            public readonly List<(
                string Name,
                int Arity,
                Match Declaration,
                int Body
            )> RouteDeclarations = new();
            private readonly Dictionary<string, bool> _namespaceShadows = new(
                StringComparer.Ordinal
            );
            private ILookup<string, (Match Declaration, int NameStart)> _receiverDeclarations;
            private ILookup<
                string,
                (Match Declaration, int NameStart)
            > _packageReceiverDeclarations;
            private ILookup<string, Match> _maskedIdentifierUses;
            private ILookup<string, (string Code, Match Name, int Offset)> _identifierUses;

            public bool HasConditionalDirectives => 0 < ConditionalDirectives.Count;

            public bool CanInspectProjectMemberUses =>
                _projectTypes != null && FindExternalCallbackUse != null;

            public bool HasAvailableDeclaration(List<int> declarations, int use)
            {
                if (declarations.Any(declaration => IsAvailableAtUse(declaration, use, default)))
                {
                    return true;
                }

                // A complete conditional supplies a declaration only if each feasible branch
                // supplies one without requiring another unresolved condition.
                foreach (ConditionalGroup group in CompleteConditionalGroups)
                {
                    TextSpan first = group.Branches[0];
                    if (!IsAvailableAtUse(group.Start, use, first))
                    {
                        continue;
                    }
                    if (
                        group.Branches.All(branch =>
                            declarations.Any(declaration =>
                                ContainsPosition(branch, declaration)
                                && IsAvailableAtUse(declaration, use, branch)
                            )
                        )
                    )
                    {
                        return true;
                    }
                }
                return false;
            }

            private bool IsAvailableAtUse(int position, int use, TextSpan branchBeingChecked)
            {
                return !UncertainBranches.Exists(branch =>
                    ContainsPosition(branch, position)
                    && !ContainsPosition(branch, use)
                    && (
                        branch.Start != branchBeingChecked.Start
                        || branch.Length != branchBeingChecked.Length
                    )
                );
            }

            private static bool ContainsPosition(TextSpan span, int position) =>
                span.Start <= position && position < span.Start + span.Length;

            public bool DeclaresQualifiedType(string masked, string name, int arity = 0) =>
                _projectTypes?.MayDeclareType(name, arity) == true
                || QualifiedTypeDeclarations(masked).Contains((name, arity))
                || _uncertainTypeDeclarations.Contains((SimpleTypeName(name), arity));

            public bool HasProjectRoute(string name, int arity, string marker) =>
                _projectTypes?.HasProvenRoute(name, arity, marker, _assemblyName) == true;

            public HashSet<(string Name, int Arity)> QualifiedTypeDeclarations(string masked)
            {
                if (_qualifiedTypeDeclarations != null)
                {
                    return _qualifiedTypeDeclarations;
                }

                _qualifiedTypeDeclarations = new();
                char[] bodyDelimiters = { '{', ';' };
                Stack<(int End, string Path)> enclosingTypes = new();
                Stack<(int End, string Path)> enclosingNamespaces = new();
                int namespaceIndex = 0;
                MatchCollection namespaces = NamespaceDeclarationRegex.Matches(masked);
                IEnumerable<Match> declarations = TypeDeclarationRegex
                    .Matches(masked)
                    .Cast<Match>()
                    .Concat(DelegateTypeDeclarationRegex.Matches(masked).Cast<Match>())
                    .OrderBy(declaration => declaration.Index);
                foreach (Match declaration in declarations)
                {
                    RemoveClosedScopes(enclosingTypes, declaration.Index);
                    string simpleName = IdentifierValue(declaration.Groups["name"].Value);
                    int arity = DeclarationArity(masked, declaration);
                    if (UncertainBranches.Count != 0)
                    {
                        // Conditional namespace and type boundaries can move the declaration.
                        _uncertainTypeDeclarations.Add((simpleName, arity));
                    }

                    while (namespaceIndex < namespaces.Count)
                    {
                        Match candidate = namespaces[namespaceIndex];
                        int boundary = candidate.Index + candidate.Length - 1;
                        if (declaration.Index <= boundary)
                        {
                            break;
                        }

                        RemoveClosedScopes(enclosingNamespaces, candidate.Index);
                        int end =
                            masked[boundary] == ';'
                                ? masked.Length
                                : FindMatching(masked, boundary, '{', '}');
                        if (0 <= end)
                        {
                            string part = NormalizeNamespacePath(candidate.Groups["path"].Value);
                            string path = Qualify(enclosingNamespaces, part);
                            enclosingNamespaces.Push((end, path));
                        }
                        namespaceIndex++;
                    }

                    RemoveClosedScopes(enclosingNamespaces, declaration.Index);
                    string qualified = Qualify(
                        enclosingTypes.Count == 0 ? enclosingNamespaces : enclosingTypes,
                        simpleName
                    );
                    if (!_qualifiedTypeDeclarations.Add((qualified, arity)))
                    {
                        DuplicateTypeDeclarations.Add((qualified, arity));
                    }
                    if (declaration.Groups["delegate"].Success)
                    {
                        continue;
                    }

                    int opening = masked.IndexOfAny(
                        bodyDelimiters,
                        declaration.Index + declaration.Length
                    );
                    if (0 <= opening && masked[opening] == '{')
                    {
                        if (
                            enclosingTypes.Count == 0
                            && !IsFileLocalDeclaration(masked, declaration)
                            && HeaderMayDeclareRoute(masked, declaration, opening)
                        )
                        {
                            RouteDeclarations.Add((qualified, arity, declaration, opening));
                        }
                        int closing = FindMatching(masked, opening, '{', '}');
                        if (closing < 0)
                        {
                            closing = masked.Length;
                        }
                        enclosingTypes.Push((closing, qualified));
                        _typeBodies.Add((opening, closing, qualified));
                    }
                }
                return _qualifiedTypeDeclarations;
            }

            public HashSet<(string Name, int Arity)> UncertainTypeDeclarations(string masked)
            {
                QualifiedTypeDeclarations(masked);
                return _uncertainTypeDeclarations;
            }

            private static void RemoveClosedScopes(
                Stack<(int End, string Path)> scopes,
                int position
            )
            {
                while (scopes.Count != 0 && scopes.Peek().End <= position)
                {
                    scopes.Pop();
                }
            }

            private static string Qualify(Stack<(int End, string Path)> scopes, string name) =>
                scopes.Count == 0 ? name : scopes.Peek().Path + "." + name;

            public bool HasCompetingTypeAtUse(
                string masked,
                string spelling,
                int arity,
                int use,
                string knownQualifiedType = null
            )
            {
                string root = spelling.Split('.')[0];
                if (
                    _projectTypes?.CanImportGlobalType(root) == true
                    || StaticImportRegex.IsMatch(masked)
                    || (
                        !spelling.Contains('.')
                        && 0 <= FindTypeParameterDeclaration(masked, spelling, use)
                    )
                )
                {
                    return true;
                }

                QualifiedTypeDeclarations(masked);
                foreach ((int start, int end, string path) in _typeBodies)
                {
                    if (
                        start < use
                        && use < end
                        && (
                            (
                                path + "." + spelling != knownQualifiedType
                                && DeclaresQualifiedType(masked, path + "." + spelling, arity)
                            )
                            || (_projectTypes == null && TypeScopeIsPartial(masked, start))
                            || (
                                GetTypeHeader(masked, start)
                                    .TrimStart()
                                    .StartsWith(":", StringComparison.Ordinal)
                                && !TypeScopeDerivesFromMessageAwareComponent(masked, start, this)
                            )
                        )
                    )
                    {
                        return true;
                    }
                }

                string scope = NamespaceAt(masked, use);
                while (scope.Length != 0)
                {
                    if (
                        scope + "." + spelling != knownQualifiedType
                        && DeclaresQualifiedType(masked, scope + "." + spelling, arity)
                    )
                    {
                        return true;
                    }
                    int dot = scope.LastIndexOf('.');
                    scope = dot < 0 ? string.Empty : scope.Substring(0, dot);
                }

                return spelling != knownQualifiedType
                    && !spelling.Contains('.')
                    && DeclaresQualifiedType(masked, spelling, arity);
            }

            public bool MayAliasType(string masked, string name) =>
                ContainsNamespaceAlias(masked, name)
                || _projectTypes?.CanImportGlobalType(name) == true;

            public bool HasNamespaceShadow(string masked, string name)
            {
                if (!_namespaceShadows.TryGetValue(name, out bool shadows))
                {
                    shadows =
                        HasTypeOrAliasDeclaration(masked, name)
                        || NamespaceDeclarationRegex
                            .Matches(masked)
                            .Cast<Match>()
                            .Any(declaration =>
                                IdentifierUseRegex
                                    .Matches(declaration.Groups["path"].Value)
                                    .Cast<Match>()
                                    .Any(part => IdentifierValue(part.Value) == name)
                            );
                    _namespaceShadows.Add(name, shadows);
                }
                return shadows;
            }

            public bool HasCompetingRegistrationExtension(string masked, Invocation invocation)
            {
                int argumentCount = SplitArguments(masked, invocation.Start, invocation.End).Count;
                foreach (Match use in MaskedIdentifierUses(masked, invocation.Name))
                {
                    int open = NextNonWhitespace(masked, use.Index + use.Length);
                    if (0 <= open && masked[open] == '<')
                    {
                        int genericClose = FindMatching(masked, open, '<', '>');
                        open = genericClose < 0 ? -1 : NextNonWhitespace(masked, genericClose + 1);
                    }
                    if (open < 0 || masked[open] != '(')
                    {
                        continue;
                    }

                    int first = SkipDeclarationAttributes(
                        masked,
                        NextNonWhitespace(masked, open + 1)
                    );
                    if (
                        !StartsWithIdentifier(masked, first, "this")
                        || first + "this".Length >= masked.Length
                        || !char.IsWhiteSpace(masked[first + "this".Length])
                    )
                    {
                        continue;
                    }

                    int close = FindMatching(masked, open, '(', ')');
                    if (close < 0)
                    {
                        return true;
                    }
                    if (
                        UncertainBranches.Exists(branch =>
                            branch.Start < close && open < branch.Start + branch.Length
                        )
                        || ExtensionCanAcceptArgumentCount(
                            masked,
                            SplitParameters(masked, open + 1, close),
                            argumentCount
                        )
                    )
                    {
                        return true;
                    }
                }
                return false;
            }

            private static bool ExtensionCanAcceptArgumentCount(
                string masked,
                List<TextSpan> parameters,
                int argumentCount
            )
            {
                int required = 0;
                bool variadic = false;
                // The first parameter is the extension receiver and is omitted at the call site.
                for (int index = 1; index < parameters.Count; index++)
                {
                    TextSpan parameter = parameters[index];
                    int end = parameter.Start + parameter.Length;
                    int first = NextNonWhitespace(masked, parameter.Start);
                    int modifier = SkipDeclarationAttributes(masked, first);
                    if (modifier < 0 || end <= modifier)
                    {
                        return true;
                    }
                    if (
                        index == parameters.Count - 1
                        && (
                            StartsWithIdentifier(masked, modifier, "params")
                            || StartsWithIdentifier(masked, modifier, "__arglist")
                        )
                    )
                    {
                        variadic = true;
                        continue;
                    }
                    // An attribute such as OptionalAttribute can make an argument optional.
                    if (first == modifier && masked.IndexOf('=', modifier, end - modifier) < 0)
                    {
                        required++;
                    }
                }
                return required <= argumentCount && (variadic || argumentCount < parameters.Count);
            }

            public IEnumerable<Match> MaskedIdentifierUses(string masked, string name)
            {
                _maskedIdentifierUses ??= IdentifierUseRegex
                    .Matches(masked)
                    .Cast<Match>()
                    .ToLookup(use => IdentifierValue(use.Value), StringComparer.Ordinal);
                return _maskedIdentifierUses[name];
            }

            public IEnumerable<(Match Declaration, int NameStart)> ReceiverDeclarations(
                string masked,
                string receiver
            )
            {
                _receiverDeclarations ??= IndexReceiverDeclarations(
                    ReceiverDeclarationRegex,
                    masked
                );
                return _receiverDeclarations[receiver];
            }

            public IEnumerable<(Match Declaration, int NameStart)> PackageReceiverDeclarations(
                string masked,
                string receiver
            )
            {
                _packageReceiverDeclarations ??= IndexReceiverDeclarations(
                    PackageReceiverDeclarationRegex,
                    masked
                );
                return _packageReceiverDeclarations[receiver];
            }

            public bool HasUnclassifiedReceiverUse(
                string masked,
                string receiver,
                int typeStart,
                IEnumerable<int> declarationNames
            )
            {
                HashSet<int> knownNames = declarationNames.ToHashSet();
                foreach (Match use in MaskedIdentifierUses(masked, receiver))
                {
                    if (
                        knownNames.Contains(use.Index)
                        || FindContainingTypeStart(masked, use.Index) != typeStart
                    )
                    {
                        continue;
                    }

                    int before = PreviousNonWhitespace(masked, use.Index - 1);
                    int after = NextNonWhitespace(masked, use.Index + use.Length);
                    if (
                        IsReceiverReference(masked, use, before, after)
                        || (
                            receiver == "value"
                            && IsImplicitAccessorValue(masked, use.Index, typeStart)
                        )
                    )
                    {
                        continue;
                    }

                    // Unknown forms can introduce an inferred lambda or pattern variable, or a
                    // deconstruction local. A same-named package field does not prove that binding.
                    return true;
                }
                return false;
            }

            private static bool IsReceiverReference(string masked, Match use, int before, int after)
            {
                if ((0 <= before && masked[before] == '.') || (0 <= after && masked[after] == '.'))
                {
                    return true;
                }

                if (
                    0 <= after
                    && masked[after] == ':'
                    && (after + 1 == masked.Length || masked[after + 1] != ':')
                )
                {
                    return true;
                }

                if (
                    0 <= after
                    && after + 1 < masked.Length
                    && masked[after] is '=' or '!'
                    && masked[after + 1] == '='
                )
                {
                    return true;
                }
                if (
                    0 <= after
                    && masked[after] == '='
                    && (after + 1 == masked.Length || masked[after + 1] != '>')
                    && (before < 0 || masked[before] is ';' or '{' or '}' or ':')
                )
                {
                    // The left side of an ordinary assignment cannot declare an inferred local.
                    return true;
                }
                if (
                    0 <= before
                    && masked[before] is '(' or ','
                    && 0 <= after
                    && masked[after] is ')' or ','
                )
                {
                    int opening = FindEnclosingOpenParenthesis(masked, use.Index);
                    int closing = 0 <= opening ? FindMatching(masked, opening, '(', ')') : -1;
                    int following = 0 <= closing ? NextNonWhitespace(masked, closing + 1) : -1;
                    // A completed argument list followed by a semicolon or conditional operator
                    // cannot declare an inferred lambda parameter or a deconstruction local.
                    return 0 <= following && masked[following] is ';' or '?';
                }
                return false;
            }

            public IEnumerable<(string Code, Match Name, int Offset)> IdentifierUses(
                string masked,
                string name
            ) => GetIdentifierUseIndex(masked)[name];

            public string FindPossibleMemberLookup(string masked)
            {
                string projectLookup = _projectTypes?.FindPossibleMemberLookup();
                if (projectLookup != null)
                {
                    return projectLookup;
                }
                foreach (var group in GetIdentifierUseIndex(masked))
                {
                    if (IsMemberLookupIdentifier(group.Key))
                    {
                        var use = group.First();
                        return SourceLocation(masked, use.Name.Index + use.Offset);
                    }
                }
                return null;
            }

            private ILookup<string, (string Code, Match Name, int Offset)> GetIdentifierUseIndex(
                string masked
            ) =>
                _identifierUses ??= EnumerateIdentifierUses(masked)
                    .ToLookup(use => IdentifierValue(use.Name.Value), StringComparer.Ordinal);

            public IEnumerable<(string Code, Match Name, int Offset)> EnumerateIdentifierUses(
                string masked
            ) =>
                FindIdentifierUses(masked, 0)
                    .Concat(
                        InterpolatedExpressions.SelectMany(expression =>
                            FindIdentifierUses(
                                MaskNonCode(
                                    Source.Substring(expression.Start, expression.Length),
                                    null
                                ),
                                expression.Start
                            )
                        )
                    );

            private static IEnumerable<(string Code, Match Name, int Offset)> FindIdentifierUses(
                string code,
                int offset
            )
            {
                foreach (Match use in IdentifierUseRegex.Matches(code))
                {
                    yield return (code, use, offset);
                }
            }

            private static ILookup<
                string,
                (Match Declaration, int NameStart)
            > IndexReceiverDeclarations(Regex pattern, string masked)
            {
                return pattern
                    .Matches(masked)
                    .Cast<Match>()
                    .SelectMany(declaration =>
                        ReceiverDeclarators(masked, declaration)
                            .Select(name =>
                                (
                                    Name: IdentifierValue(name.Value),
                                    Declaration: declaration,
                                    NameStart: name.Index
                                )
                            )
                    )
                    .ToLookup(
                        entry => entry.Name,
                        entry => (entry.Declaration, entry.NameStart),
                        StringComparer.Ordinal
                    );
            }

            public static IEnumerable<Group> ReceiverDeclarators(string masked, Match declaration)
            {
                Group name = declaration.Groups["name"];
                yield return name;
                int next = NextNonWhitespace(masked, name.Index + name.Length);
                while (0 <= next && next < masked.Length)
                {
                    if (
                        masked[next] == '='
                        && (next + 1 == masked.Length || masked[next + 1] != '>')
                    )
                    {
                        next = FindExpressionEnd(masked, next + 1);
                    }
                    if (masked.Length <= next || masked[next] != ',')
                    {
                        yield break;
                    }
                    int start = NextNonWhitespace(masked, next + 1);
                    if (start < 0)
                    {
                        yield break;
                    }
                    Match additional = DirectMemberIdentifierRegex.Match(masked, start);
                    if (!additional.Success)
                    {
                        yield break;
                    }
                    next = NextNonWhitespace(masked, start + additional.Length);
                    if (next < 0 || masked[next] is not ('=' or ',' or ';'))
                    {
                        // A following parameter carries its own type.
                        yield break;
                    }
                    yield return additional;
                }
            }
        }

        private sealed class ConditionalMask
        {
            private readonly Stack<(
                int Start,
                BranchState Outer,
                BranchState Remaining,
                ConditionalGroup Group
            )> _scopes = new();
            private readonly SourceMetadata _metadata;
            private int _uncertainTail = int.MaxValue;

            public ConditionalMask(SourceMetadata metadata)
            {
                _metadata = metadata;
            }

            public BranchState State { get; private set; } = BranchState.Active;

            public void Visit(string keyword, ReadOnlySpan<char> condition, int start)
            {
                _metadata.ConditionalDirectives.Add(start);
                BranchState outer = State;
                BranchState remaining = outer;
                ConditionalGroup group;
                if (keyword != "if")
                {
                    if (_scopes.Count == 0)
                    {
                        _uncertainTail = Math.Min(_uncertainTail, start);
                        return;
                    }
                    var scope = _scopes.Pop();
                    outer = scope.Outer;
                    remaining = scope.Remaining;
                    group = scope.Group;
                    if (State != BranchState.Inactive)
                    {
                        group.Branches.Add(new TextSpan(scope.Start, start - scope.Start));
                    }
                    RecordBranch(scope.Start, start);
                }
                else
                {
                    group = new ConditionalGroup(start);
                }

                if (keyword == "endif")
                {
                    if (remaining == BranchState.Inactive && group.Branches.Count != 0)
                    {
                        _metadata.CompleteConditionalGroups.Add(group);
                    }
                    State = outer;
                    return;
                }

                BranchState selected =
                    keyword == "else" ? BranchState.Active : ReadCondition(condition);
                BranchState unselected = selected switch
                {
                    BranchState.Active => BranchState.Inactive,
                    BranchState.Inactive => BranchState.Active,
                    _ => BranchState.Uncertain,
                };
                _scopes.Push((start, outer, Intersect(remaining, unselected), group));
                State = Intersect(remaining, selected);
            }

            public void ObserveMaskedToken(ReadOnlySpan<char> token, int tokenStart)
            {
                int firstLineEnd = token.IndexOfAny(CSharpLineTerminators.AsSpan());
                if (firstLineEnd < 0)
                {
                    return;
                }
                for (int next = firstLineEnd + 1; next < token.Length; )
                {
                    int offset = token.Slice(next).IndexOf('#');
                    if (offset < 0)
                    {
                        return;
                    }
                    int hash = next + offset;
                    if (
                        IsDirectiveStart(token, hash)
                        && ReadConditionalKeyword(
                            token,
                            hash,
                            FindDirectiveLineEnd(token, hash),
                            out _
                        ) != null
                    )
                    {
                        // A directive-position hash can become active when this token is disabled.
                        // This can conceal a use of a callback declared earlier in the script.
                        _metadata.UncertainDirectivePosition ??= tokenStart + hash;
                        return;
                    }
                    next = hash + 1;
                }
            }

            public void Complete(char[] masked)
            {
                while (0 < _scopes.Count)
                {
                    (int start, BranchState outer, _, _) = _scopes.Pop();
                    RecordBranch(start, masked.Length);
                    State = outer;
                }
                foreach (
                    TextSpan branch in _metadata.UncertainBranches.Where(branch =>
                        !IsContainedBranch(masked, branch)
                    )
                )
                {
                    _uncertainTail = Math.Min(_uncertainTail, branch.Start);
                }
                if (_uncertainTail < masked.Length)
                {
                    _metadata.UncertainBranches.Add(
                        new TextSpan(_uncertainTail, masked.Length - _uncertainTail)
                    );
                }
            }

            private void RecordBranch(int start, int end)
            {
                if (State == BranchState.Uncertain)
                {
                    _metadata.UncertainBranches.Add(new TextSpan(start, end - start));
                }
            }

            private static BranchState ReadCondition(ReadOnlySpan<char> condition)
            {
                int comment = condition.IndexOf("//".AsSpan(), StringComparison.Ordinal);
                ReadOnlySpan<char> expression = (
                    comment < 0 ? condition : condition.Slice(0, comment)
                ).Trim();
                if (expression.SequenceEqual("true".AsSpan()))
                {
                    return BranchState.Active;
                }
                return expression.SequenceEqual("false".AsSpan())
                    ? BranchState.Inactive
                    : BranchState.Uncertain;
            }

            private static BranchState Intersect(BranchState available, BranchState branch)
            {
                if (available == BranchState.Inactive || branch == BranchState.Inactive)
                {
                    return BranchState.Inactive;
                }
                return available == BranchState.Active ? branch : BranchState.Uncertain;
            }

            private static bool IsContainedBranch(ReadOnlySpan<char> masked, TextSpan branch)
            {
                int end = branch.Start + branch.Length;
                Stack<char> delimiters = new();
                char last = '\0';
                for (int index = branch.Start; index < end; index++)
                {
                    char value = masked[index];
                    if (char.IsWhiteSpace(value))
                    {
                        continue;
                    }

                    last = value;
                    char expected = value switch
                    {
                        '(' => ')',
                        '[' => ']',
                        '{' => '}',
                        _ => '\0',
                    };
                    if (expected != '\0')
                    {
                        delimiters.Push(expected);
                    }
                    if (
                        value is ')' or ']' or '}'
                        && (delimiters.Count == 0 || delimiters.Pop() != value)
                    )
                    {
                        return false;
                    }
                }

                return delimiters.Count == 0 && last is '\0' or ';' or '}';
            }
        }

        private static int FindNonCodeEnd(
            string source,
            int start,
            List<TextSpan> interpolatedExpressions
        )
        {
            if (source[start] == '\'')
            {
                return FindQuotedEnd(source, start + 1, '\'', verbatim: false);
            }
            if (
                source[start] == '/'
                && start + 1 < source.Length
                && source[start + 1] is '/' or '*'
            )
            {
                int end = FindCommentEnd(source, start, source.Length);
                return end < 0 ? source.Length : end;
            }

            return FindStringEnd(source, start, interpolatedExpressions);
        }

        private static int FindStringEnd(
            string source,
            int start,
            List<TextSpan> interpolatedExpressions
        )
        {
            int quoteStart = start;
            bool verbatim = source[start] == '@';
            bool interpolated = source[start] == '$';
            int interpolationDollarCount = interpolated ? CountRun(source, start, '$') : 0;
            if (verbatim || interpolated)
            {
                quoteStart += verbatim ? 1 : interpolationDollarCount;
                char alternatePrefix = verbatim ? '$' : '@';
                if (quoteStart < source.Length && source[quoteStart] == alternatePrefix)
                {
                    quoteStart++;
                    verbatim = true;
                    interpolated = true;
                }
            }
            if (source.Length <= quoteStart || source[quoteStart] != '"')
            {
                return start;
            }

            // The opening quote of a verbatim string can be followed by a doubled quote.
            int quoteCount = verbatim ? 1 : CountRun(source, quoteStart, '"');
            if (quoteCount < 3)
            {
                return interpolated
                    ? FindInterpolatedStringEnd(
                        source,
                        quoteStart + 1,
                        verbatim,
                        interpolatedExpressions
                    )
                    : FindQuotedEnd(source, quoteStart + 1, '"', verbatim);
            }

            return FindRawStringEnd(
                source,
                quoteStart + quoteCount,
                quoteCount,
                interpolationDollarCount,
                interpolatedExpressions
            );
        }

        private static int FindInterpolatedStringEnd(
            string source,
            int start,
            bool verbatim,
            List<TextSpan> interpolatedExpressions
        )
        {
            for (int index = start; index < source.Length; index++)
            {
                char current = source[index];
                if (!verbatim && CSharpLineTerminators.Contains(current))
                {
                    return index;
                }

                if (!verbatim && StartsQuotedEscape(source, index))
                {
                    index++;
                }
                else if (current == '"')
                {
                    if (!verbatim || source.Length <= index + 1 || source[index + 1] != '"')
                    {
                        return index + 1;
                    }
                    index++;
                }
                else if (current == '{' && index + 1 < source.Length)
                {
                    if (source[index + 1] == '{')
                    {
                        index++;
                        continue;
                    }

                    index = ReadInterpolation(source, index + 1, interpolatedExpressions);
                }
            }
            return source.Length;
        }

        private static int ReadInterpolation(
            string source,
            int start,
            List<TextSpan> interpolatedExpressions,
            int closingBraceCount = 1
        )
        {
            int close = FindInterpolationEnd(
                source,
                start,
                interpolatedExpressions,
                out int expressionEnd,
                closingBraceCount
            );
            if (close < source.Length && start < expressionEnd)
            {
                interpolatedExpressions?.Add(new TextSpan(start, expressionEnd - start));
            }
            return close;
        }

        private static int FindInterpolationEnd(
            string source,
            int start,
            List<TextSpan> interpolatedExpressions,
            out int expressionEnd,
            int closingBraceCount
        )
        {
            int nesting = 0;
            for (int index = start; index < source.Length; index++)
            {
                int end = FindNonCodeEnd(source, index, interpolatedExpressions);
                if (index < end)
                {
                    index = end - 1;
                    continue;
                }

                switch (source[index])
                {
                    case '(' or '[' or '{':
                        nesting++;
                        break;
                    case '}'
                        when nesting == 0
                            && (
                                closingBraceCount == 1
                                || closingBraceCount <= CountRun(source, index, '}')
                            ):
                        expressionEnd = index;
                        return index;
                    case ')' or ']' or '}':
                        nesting--;
                        break;
                    case ':' when nesting == 0:
                        if (index + 1 < source.Length && source[index + 1] == ':')
                        {
                            // Global and namespace aliases use :: within expressions.
                            index++;
                            break;
                        }

                        expressionEnd = index;
                        int close =
                            closingBraceCount == 1
                                ? source.IndexOf('}', index + 1)
                                : source.IndexOf(
                                    new string('}', closingBraceCount),
                                    index + 1,
                                    StringComparison.Ordinal
                                );
                        return close < 0 ? source.Length : close;
                }
            }
            expressionEnd = source.Length;
            return source.Length;
        }

        private static int FindCommentEnd(string source, int start, int limit)
        {
            int contentStart = start + 2;
            int length = limit - contentStart;
            if (source[start + 1] == '/')
            {
                int newline = source
                    .AsSpan(contentStart, length)
                    .IndexOfAny(CSharpLineTerminators.AsSpan());
                return newline < 0 ? limit : contentStart + newline;
            }
            if (source[start + 1] != '*')
            {
                return -1;
            }

            int closing = source.IndexOf("*/", contentStart, length, StringComparison.Ordinal);
            return closing < 0 ? -1 : closing + 2;
        }

        private static int FindQuotedEnd(string source, int start, char quote, bool verbatim)
        {
            for (int index = start; index < source.Length; index++)
            {
                char current = source[index];
                if (!verbatim && CSharpLineTerminators.Contains(current))
                {
                    // An unmatched ordinary quote in skipped conditional text ends on this line.
                    return index;
                }

                if (current != quote)
                {
                    if (!verbatim && StartsQuotedEscape(source, index))
                    {
                        index++;
                    }
                    continue;
                }
                if (verbatim && index + 1 < source.Length && source[index + 1] == quote)
                {
                    index++;
                    continue;
                }
                return index + 1;
            }
            return source.Length;
        }

        private static bool StartsQuotedEscape(string source, int position) =>
            source[position] == '\\'
            && position + 1 < source.Length
            && !CSharpLineTerminators.Contains(source[position + 1]);

        private static int FindRawStringEnd(
            string source,
            int start,
            int quoteCount,
            int interpolationDollarCount,
            List<TextSpan> interpolatedExpressions
        )
        {
            if (interpolationDollarCount == 0)
            {
                string terminator = new('"', quoteCount);
                int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
                return end < 0 ? source.Length : end + quoteCount;
            }

            for (int index = start; index < source.Length; )
            {
                if (source[index] == '"')
                {
                    int quoteRun = CountRun(source, index, '"');
                    if (quoteCount <= quoteRun)
                    {
                        return index + quoteCount;
                    }
                    index += quoteRun;
                    continue;
                }
                if (source[index] != '{')
                {
                    index++;
                    continue;
                }

                int openingBraceCount = CountRun(source, index, '{');
                index += openingBraceCount;
                if (interpolationDollarCount <= openingBraceCount)
                {
                    // A quote run within an interpolation belongs to the expression.
                    int closing = ReadInterpolation(
                        source,
                        index,
                        interpolatedExpressions,
                        interpolationDollarCount
                    );
                    index = closing == source.Length ? closing : closing + interpolationDollarCount;
                }
            }
            return source.Length;
        }

        private static int CountRun(string source, int start, char value)
        {
            int end = start;
            while (end < source.Length && source[end] == value)
            {
                end++;
            }
            return end - start;
        }

        private static void Mask(char[] text, int start, int end)
        {
            for (int index = start; index < end; index++)
            {
                if (!CSharpLineTerminators.Contains(text[index]))
                {
                    text[index] = ' ';
                }
            }
        }

        private static int FindMatching(string text, int open, char openValue, char closeValue)
        {
            int depth = 0;
            for (int index = open; index < text.Length; index++)
            {
                if (text[index] == openValue)
                {
                    depth++;
                }
                else if (text[index] == closeValue && --depth == 0)
                {
                    return index;
                }
            }
            return -1;
        }

        private static int FindMatchingBackward(
            string text,
            int close,
            char openValue,
            char closeValue,
            int minimum = 0
        )
        {
            int depth = 0;
            for (int index = close; minimum <= index; index--)
            {
                if (text[index] == closeValue)
                {
                    depth++;
                }
                else if (text[index] == openValue && --depth == 0)
                {
                    return index;
                }
            }
            return -1;
        }

        private static int NextNonWhitespace(string text, int start)
        {
            for (int index = start; index < text.Length; index++)
            {
                if (!char.IsWhiteSpace(text[index]))
                {
                    return index;
                }
            }
            return -1;
        }

        private static int PreviousNonWhitespace(string text, int start)
        {
            for (int index = start; 0 <= index; index--)
            {
                if (!char.IsWhiteSpace(text[index]))
                {
                    return index;
                }
            }
            return -1;
        }

        private static bool StartsWithIdentifier(string text, int start, string identifier)
        {
            if (
                start < 0
                || text.Length < start + identifier.Length
                || !text.AsSpan(start, identifier.Length).SequenceEqual(identifier.AsSpan())
            )
            {
                return false;
            }
            int end = start + identifier.Length;
            return end == text.Length || !IsIdentifierPart(text[end]);
        }

        private static bool IsIdentifierPart(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        private static bool TryClassifyFastDelegateType(
            string text,
            int fastHandlerStart,
            SourceMetadata metadata,
            out bool isProven,
            out bool hasHandlerSyntax
        )
        {
            isProven = false;
            hasHandlerSyntax = false;
            Match handler = DirectMemberIdentifierRegex.Match(text, fastHandlerStart);
            if (
                !handler.Success
                || IdentifierValue(handler.Value) is not ("FastHandler" or "FastHandlerWithContext")
            )
            {
                return false;
            }
            int genericOpen = NextNonWhitespace(text, fastHandlerStart + handler.Length);
            if (genericOpen < 0 || text[genericOpen] != '<')
            {
                return false;
            }

            int dot = PreviousNonWhitespace(text, fastHandlerStart - 1);
            if (dot < 0 || text[dot] != '.')
            {
                return false;
            }
            int identifierEnd = PreviousNonWhitespace(text, dot - 1);
            int identifierStart = FindIdentifierStart(text, identifierEnd);
            if (
                identifierStart < 0
                || IdentifierValue(
                    text.Substring(identifierStart, identifierEnd - identifierStart + 1)
                ) != "MessageHandler"
            )
            {
                return false;
            }
            hasHandlerSyntax = true;

            const string PackageType = "DxMessaging.Core.MessageHandler";
            if (HasDxMessagingCoreQualifier(text, identifierStart, out bool globallyQualified))
            {
                string spelling = globallyQualified ? "global::" + PackageType : PackageType;
                isProven = IsKnownType(text, metadata, spelling, PackageType, identifierStart);
                return true;
            }

            int handlerQualifier = PreviousNonWhitespace(text, identifierStart - 1);
            if (
                (0 <= handlerQualifier && text[handlerQualifier] is '.' or ':')
                || !HasNamespaceImport(text, "DxMessaging.Core", hasConditionalDirectives: false)
            )
            {
                return false;
            }

            // A possible import identifies a review candidate but does not prove its binding.
            isProven = IsKnownType(text, metadata, "MessageHandler", PackageType, identifierStart);
            return true;
        }

        private static bool ContainsNamespaceAlias(string text, string name)
        {
            try
            {
                return NamespaceAliasRegex
                    .Matches(text)
                    .Cast<Match>()
                    .Any(alias => IdentifierValue(alias.Groups["alias"].Value) == name);
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }
        }

        private static bool HasDxMessagingCoreQualifier(
            string text,
            int messageHandlerStart,
            out bool globallyQualified
        )
        {
            globallyQualified = false;
            int coreDot = PreviousNonWhitespace(text, messageHandlerStart - 1);
            if (coreDot < 0 || text[coreDot] != '.')
            {
                return false;
            }
            int coreEnd = PreviousNonWhitespace(text, coreDot - 1);
            int coreStart = FindIdentifierStart(text, coreEnd);
            if (
                coreStart < 0
                || IdentifierValue(text.Substring(coreStart, coreEnd - coreStart + 1)) != "Core"
            )
            {
                return false;
            }

            int dxMessagingDot = PreviousNonWhitespace(text, coreStart - 1);
            if (dxMessagingDot < 0 || text[dxMessagingDot] != '.')
            {
                return false;
            }

            int dxMessagingEnd = PreviousNonWhitespace(text, dxMessagingDot - 1);
            int dxMessagingStart = FindIdentifierStart(text, dxMessagingEnd);
            if (
                dxMessagingStart < 0
                || IdentifierValue(
                    text.Substring(dxMessagingStart, dxMessagingEnd - dxMessagingStart + 1)
                ) != "DxMessaging"
            )
            {
                return false;
            }

            int qualifierPrefix = PreviousNonWhitespace(text, dxMessagingStart - 1);
            globallyQualified =
                0 <= qualifierPrefix
                && text[qualifierPrefix] == ':'
                && Regex.IsMatch(
                    text.Substring(0, dxMessagingStart),
                    @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])global\s*::\s*$",
                    RegexOptions.CultureInvariant
                );
            return globallyQualified
                || qualifierPrefix < 0
                || text[qualifierPrefix] is not ('.' or ':' or '@');
        }

        private static void ApplyAtomically(
            List<FileUpgrade> upgrades,
            string assetsRoot,
            Action verifyBeforeCommit
        )
        {
            List<PendingFileUpgrade> pending = new(upgrades.Count);
            foreach (FileUpgrade upgrade in upgrades)
            {
                pending.Add(
                    new PendingFileUpgrade(
                        upgrade.FullPath,
                        upgrade.OriginalBytes,
                        upgrade.UpgradedBytes
                    )
                );
            }

            ApplyAtomically(pending, new PhysicalAtomicFileStore(assetsRoot), verifyBeforeCommit);
        }

        internal static void ApplyAtomically(
            IReadOnlyList<PendingFileUpgrade> upgrades,
            IAtomicFileStore store,
            Action verifyBeforeCommit = null
        )
        {
            if (upgrades == null)
            {
                throw new ArgumentNullException(nameof(upgrades));
            }
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            List<AppliedFile> applied = new(upgrades.Count);
            try
            {
                foreach (PendingFileUpgrade upgrade in upgrades)
                {
                    string backup = store.Replace(
                        upgrade.FullPath,
                        upgrade.OriginalBytes,
                        upgrade.UpgradedBytes
                    );
                    applied.Add(new AppliedFile(upgrade.FullPath, backup, upgrade.UpgradedBytes));
                }
                verifyBeforeCommit?.Invoke();
            }
            catch (Exception writeFailure)
            {
                List<Exception> rollbackFailures = new();
                for (int index = applied.Count - 1; 0 <= index; index--)
                {
                    AppliedFile file = applied[index];
                    try
                    {
                        store.Restore(file.FullPath, file.BackupPath, file.UpgradedBytes);
                    }
                    catch (Exception rollbackFailure)
                    {
                        rollbackFailures.Add(rollbackFailure);
                    }
                }

                if (rollbackFailures.Count == 0)
                {
                    throw;
                }
                rollbackFailures.Insert(0, writeFailure);
                throw new AggregateException(
                    "The fast-handler upgrade failed and at least one file could not be restored. "
                        + "Backup files were preserved beside the affected scripts.",
                    rollbackFailures
                );
            }

            foreach (AppliedFile file in applied)
            {
                try
                {
                    store.DiscardBackup(file.BackupPath);
                }
                catch (Exception cleanupFailure)
                {
                    DxMessagingEditorLog.LogWarning(
                        $"Updated {file.FullPath}, but could not remove backup {file.BackupPath}.",
                        cleanupFailure
                    );
                }
            }
        }

        private static void LogManualReview(SortedSet<string> manualReview)
        {
            if (manualReview.Count == 0)
            {
                return;
            }
            Debug.LogWarning(
                "[DxMessaging] The fast-handler upgrade tool left these items unchanged:\n- "
                    + string.Join("\n- ", manualReview)
            );
        }

        internal readonly struct UpgradeResult
        {
            public UpgradeResult(
                string upgradedSource,
                int replacementCount,
                int stringHandlerOptInCount,
                IReadOnlyList<string> manualReviewMethods
            )
            {
                UpgradedSource = upgradedSource;
                ReplacementCount = replacementCount;
                StringHandlerOptInCount = stringHandlerOptInCount;
                ManualReviewMethods = manualReviewMethods;
            }

            public string UpgradedSource { get; }

            public int ReplacementCount { get; }

            public int StringHandlerOptInCount { get; }

            public IReadOnlyList<string> ManualReviewMethods { get; }
        }

        private readonly struct Invocation
        {
            public Invocation(string name, int start, int end)
            {
                Name = name;
                Start = start;
                End = end;
            }

            public string Name { get; }

            public int Start { get; }

            public int End { get; }
        }

        private readonly struct ParameterList
        {
            public ParameterList(int declarationStart, int parametersStart, int parametersEnd)
            {
                DeclarationStart = declarationStart;
                ParametersStart = parametersStart;
                ParametersEnd = parametersEnd;
            }

            public int DeclarationStart { get; }

            public int ParametersStart { get; }

            public int ParametersEnd { get; }
        }

        private readonly struct TextSpan
        {
            public TextSpan(int start, int length)
            {
                Start = start;
                Length = length;
            }

            public int Start { get; }

            public int Length { get; }
        }

        internal sealed class ProjectCallbackUses
        {
            private readonly Dictionary<
                string,
                (
                    (string Path, string Source, int Position) First,
                    (string Path, string Source, int Position) Second
                )
            > _uses = new(StringComparer.Ordinal);

            private readonly HashSet<(string Name, int Arity)> _typeDeclarations = new();
            private readonly HashSet<(string Name, int Arity)> _uncertainTypeDeclarations = new();
            private readonly HashSet<(string Name, int Arity)> _duplicateTypes = new();
            private readonly Dictionary<
                (string Name, int Arity),
                (
                    string Masked,
                    SourceMetadata Metadata,
                    Match Declaration,
                    int Body,
                    string AssemblyName
                )
            > _routeDeclarations = new();
            private readonly HashSet<string> _globalAliases = new(StringComparer.Ordinal);
            private (string Path, string Source, int Position) _possibleMemberLookup;
            private string _unavailableScript;

            public bool MayDeclareType(string name, int arity) =>
                _unavailableScript != null
                || _typeDeclarations.Contains((name, arity))
                || _uncertainTypeDeclarations.Contains((SimpleTypeName(name), arity));

            public bool HasProvenRoute(
                string name,
                int arity,
                string marker,
                string assemblyName
            ) =>
                !string.IsNullOrEmpty(assemblyName)
                && _unavailableScript == null
                && !_duplicateTypes.Contains((name, arity))
                && !_uncertainTypeDeclarations.Contains((SimpleTypeName(name), arity))
                && _routeDeclarations.TryGetValue((name, arity), out var route)
                && route.AssemblyName == assemblyName
                && HasDirectRouteMarker(
                    route.Masked,
                    route.Metadata,
                    route.Declaration,
                    route.Body,
                    marker
                );

            private bool _hasGlobalStaticImport;

            public bool CanImportGlobalType(string name) =>
                _hasGlobalStaticImport || _globalAliases.Contains(name);

            public void AddScript(string path, string source, string assemblyName = null)
            {
                SourceMetadata metadata = new(source, projectTypes: this);
                string masked = MaskNonCode(source, metadata.InterpolatedExpressions, metadata);
                if (metadata.UncertainDirectivePosition is int directive)
                {
                    _unavailableScript ??=
                        $"{path}, {SourceLocation(source, directive)} (conditional compilation changes this script's boundaries)";
                    return;
                }

                foreach ((string name, int arity) in metadata.QualifiedTypeDeclarations(masked))
                {
                    if (!_typeDeclarations.Add((name, arity)))
                    {
                        _duplicateTypes.Add((name, arity));
                    }
                }
                _duplicateTypes.UnionWith(metadata.DuplicateTypeDeclarations);
                _uncertainTypeDeclarations.UnionWith(metadata.UncertainTypeDeclarations(masked));
                if (!string.IsNullOrEmpty(assemblyName))
                {
                    foreach (var route in metadata.RouteDeclarations)
                    {
                        _routeDeclarations.TryAdd(
                            (route.Name, route.Arity),
                            (masked, metadata, route.Declaration, route.Body, assemblyName)
                        );
                    }
                }
                _hasGlobalStaticImport |= StaticImportRegex
                    .Matches(masked)
                    .Cast<Match>()
                    .Any(import =>
                        FollowsKeyword(
                            masked,
                            0,
                            PreviousNonWhitespace(masked, import.Index - 1),
                            "global"
                        )
                    );
                foreach (Match alias in NamespaceAliasRegex.Matches(masked))
                {
                    int prefix = PreviousNonWhitespace(masked, alias.Index - 1);
                    if (FollowsKeyword(masked, 0, prefix, "global"))
                    {
                        _globalAliases.Add(IdentifierValue(alias.Groups["alias"].Value));
                    }
                }
                foreach ((_, Match name, int offset) in metadata.EnumerateIdentifierUses(masked))
                {
                    string identifier = IdentifierValue(name.Value);
                    if (_possibleMemberLookup.Path == null && IsMemberLookupIdentifier(identifier))
                    {
                        _possibleMemberLookup = (path, source, name.Index + offset);
                    }
                    _uses.TryGetValue(identifier, out var uses);
                    if (uses.First.Path == null)
                    {
                        uses.First = (path, source, name.Index + offset);
                    }
                    else if (uses.First.Path != path && uses.Second.Path == null)
                    {
                        uses.Second = (path, source, name.Index + offset);
                    }
                    else
                    {
                        continue;
                    }
                    _uses[identifier] = uses;
                }
            }

            public string FindPossibleMemberLookup() =>
                _possibleMemberLookup.Path == null
                    ? null
                    : $"{_possibleMemberLookup.Path}, {SourceLocation(_possibleMemberLookup.Source, _possibleMemberLookup.Position)}";

            public void MarkUnreadable(string path) =>
                _unavailableScript ??= $"{path} (unreadable script)";

            public string FindOtherUse(string ownPath, string name)
            {
                if (_uses.TryGetValue(name, out var uses))
                {
                    var other = uses.First.Path == ownPath ? uses.Second : uses.First;
                    if (other.Path != null)
                    {
                        return $"{other.Path}, {SourceLocation(other.Source, other.Position)}";
                    }
                }
                return _unavailableScript;
            }
        }

        private readonly struct FileUpgrade
        {
            public FileUpgrade(
                string fullPath,
                byte[] originalBytes,
                byte[] upgradedBytes,
                int replacementCount,
                int stringHandlerOptInCount
            )
            {
                FullPath = fullPath;
                OriginalBytes = originalBytes;
                UpgradedBytes = upgradedBytes;
                ReplacementCount = replacementCount;
                StringHandlerOptInCount = stringHandlerOptInCount;
            }

            public string FullPath { get; }

            public byte[] OriginalBytes { get; }

            public byte[] UpgradedBytes { get; }

            public int ReplacementCount { get; }

            public int StringHandlerOptInCount { get; }
        }

        internal readonly struct PendingFileUpgrade
        {
            public PendingFileUpgrade(string fullPath, byte[] originalBytes, byte[] upgradedBytes)
            {
                FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
                OriginalBytes = originalBytes
                    ?? throw new ArgumentNullException(nameof(originalBytes));
                UpgradedBytes = upgradedBytes
                    ?? throw new ArgumentNullException(nameof(upgradedBytes));
            }

            public string FullPath { get; }

            public byte[] OriginalBytes { get; }

            public byte[] UpgradedBytes { get; }
        }

        internal interface IAtomicFileStore
        {
            string Replace(string fullPath, byte[] originalBytes, byte[] upgradedBytes);

            void Restore(string fullPath, string backupPath, byte[] expectedCurrentBytes);

            void DiscardBackup(string backupPath);
        }

        internal sealed class PhysicalAtomicFileStore : IAtomicFileStore
        {
            private readonly string _assetsRoot;

            public PhysicalAtomicFileStore(string assetsRoot = null)
            {
                _assetsRoot = assetsRoot;
            }

            private void ValidatePath(string fullPath)
            {
                if (_assetsRoot != null)
                {
                    ValidateProjectPath(_assetsRoot, fullPath);
                }
            }

            public string Replace(string fullPath, byte[] originalBytes, byte[] upgradedBytes)
            {
                ValidatePath(fullPath);
                byte[] currentBytes = File.ReadAllBytes(fullPath);
                if (!currentBytes.AsSpan().SequenceEqual(originalBytes))
                {
                    throw new IOException(
                        $"{fullPath} changed after the upgrade preview. No newer content was overwritten."
                    );
                }
                string backupPath = fullPath + ".dxmsg-backup-" + Guid.NewGuid().ToString("N");
                string temporaryPath = fullPath + ".dxmsg-new-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temporaryPath, upgradedBytes);
                try
                {
                    File.Replace(temporaryPath, fullPath, backupPath);
                }
                finally
                {
                    TryDelete(temporaryPath);
                }
                bool backupMatchesOriginal;
                bool destinationMatchesUpgrade;
                try
                {
                    backupMatchesOriginal = File.ReadAllBytes(backupPath)
                        .AsSpan()
                        .SequenceEqual(originalBytes);
                    destinationMatchesUpgrade = File.ReadAllBytes(fullPath)
                        .AsSpan()
                        .SequenceEqual(upgradedBytes);
                }
                catch (Exception verificationFailure)
                {
                    string recoveryPath =
                        fullPath + ".dxmsg-unverified-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Replace(backupPath, fullPath, recoveryPath);
                    }
                    catch (Exception recoveryFailure)
                    {
                        throw new AggregateException(
                            $"{fullPath} was replaced, but verification and recovery both failed. "
                                + $"Inspect {backupPath} before continuing.",
                            verificationFailure,
                            recoveryFailure
                        );
                    }
                    throw new IOException(
                        $"{fullPath} could not be verified after replacement. Its pre-replacement "
                            + $"content was restored, and the replaced content remains at {recoveryPath}.",
                        verificationFailure
                    );
                }
                if (!backupMatchesOriginal)
                {
                    string recoveryPath =
                        fullPath + ".dxmsg-recovery-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Replace(backupPath, fullPath, recoveryPath);
                    }
                    catch (Exception recoveryFailure)
                    {
                        throw new IOException(
                            $"{fullPath} changed during replacement. The newer content remains at "
                                + $"{backupPath}; the upgrade could not restore it automatically.",
                            recoveryFailure
                        );
                    }

                    bool recoveryContainsOnlyUpgrade = File.ReadAllBytes(recoveryPath)
                        .AsSpan()
                        .SequenceEqual(upgradedBytes);
                    if (recoveryContainsOnlyUpgrade)
                    {
                        TryDelete(recoveryPath);
                    }
                    throw new IOException(
                        recoveryContainsOnlyUpgrade
                            ? $"{fullPath} changed during replacement. The newer content was restored."
                            : $"{fullPath} changed during replacement. One edit was restored, and "
                                + $"another remains at {recoveryPath}."
                    );
                }
                if (!destinationMatchesUpgrade)
                {
                    throw new IOException(
                        $"{fullPath} changed immediately after replacement. The newer content was "
                            + $"preserved, and the pre-upgrade content remains at {backupPath}."
                    );
                }
                return backupPath;
            }

            public void Restore(string fullPath, string backupPath, byte[] expectedCurrentBytes)
            {
                if (!File.Exists(backupPath))
                {
                    throw new FileNotFoundException(
                        "The fast-handler upgrade backup is missing.",
                        backupPath
                    );
                }
                ValidatePath(fullPath);
                ValidatePath(backupPath);
                byte[] currentBytes = File.ReadAllBytes(fullPath);
                if (!currentBytes.AsSpan().SequenceEqual(expectedCurrentBytes))
                {
                    throw new IOException(
                        $"{fullPath} changed while the upgrade was being rolled back. The newer "
                            + $"content was preserved, and the original remains at {backupPath}."
                    );
                }
                string rollbackSafetyPath =
                    fullPath + ".dxmsg-rollback-safety-" + Guid.NewGuid().ToString("N");
                File.Replace(backupPath, fullPath, rollbackSafetyPath);
                if (
                    File.ReadAllBytes(rollbackSafetyPath)
                        .AsSpan()
                        .SequenceEqual(expectedCurrentBytes)
                )
                {
                    TryDelete(rollbackSafetyPath);
                    return;
                }

                string recoveryPath =
                    fullPath + ".dxmsg-rollback-recovery-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Replace(rollbackSafetyPath, fullPath, recoveryPath);
                }
                catch (Exception recoveryFailure)
                {
                    throw new IOException(
                        $"{fullPath} changed during rollback. The newer content remains at "
                            + $"{rollbackSafetyPath}.",
                        recoveryFailure
                    );
                }
                throw new IOException(
                    $"{fullPath} changed during rollback. The newer content was restored, and the "
                        + $"pre-upgrade content remains at {recoveryPath}."
                );
            }

            public void DiscardBackup(string backupPath)
            {
                if (_assetsRoot != null && File.Exists(backupPath))
                {
                    ValidatePath(backupPath);
                }
                TryDelete(backupPath);
            }

            private static void TryDelete(string path)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private readonly struct AppliedFile
        {
            public AppliedFile(string fullPath, string backupPath, byte[] upgradedBytes)
            {
                FullPath = fullPath;
                BackupPath = backupPath;
                UpgradedBytes = upgradedBytes;
            }

            public string FullPath { get; }

            public string BackupPath { get; }

            public byte[] UpgradedBytes { get; }
        }

        private readonly struct EncodedSource
        {
            private readonly Encoding _encoding;
            private readonly byte[] _preamble;

            private EncodedSource(string text, Encoding encoding, byte[] preamble)
            {
                Text = text;
                _encoding = encoding;
                _preamble = preamble;
            }

            public string Text { get; }

            public static EncodedSource FromBytes(byte[] bytes)
            {
                Encoding encoding = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true
                );
                int preambleLength = 0;
                if (StartsWith(bytes, Encoding.UTF8.GetPreamble()))
                {
                    encoding = new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true
                    );
                    preambleLength = Encoding.UTF8.GetPreamble().Length;
                }
                else if (StartsWith(bytes, Encoding.UTF32.GetPreamble()))
                {
                    encoding = new UTF32Encoding(
                        bigEndian: false,
                        byteOrderMark: false,
                        throwOnInvalidCharacters: true
                    );
                    preambleLength = Encoding.UTF32.GetPreamble().Length;
                }
                else if (StartsWith(bytes, new UTF32Encoding(true, true).GetPreamble()))
                {
                    Encoding bigEndianUtf32 = new UTF32Encoding(true, true);
                    encoding = new UTF32Encoding(
                        bigEndian: true,
                        byteOrderMark: false,
                        throwOnInvalidCharacters: true
                    );
                    preambleLength = bigEndianUtf32.GetPreamble().Length;
                }
                else if (StartsWith(bytes, Encoding.Unicode.GetPreamble()))
                {
                    encoding = new UnicodeEncoding(
                        bigEndian: false,
                        byteOrderMark: false,
                        throwOnInvalidBytes: true
                    );
                    preambleLength = Encoding.Unicode.GetPreamble().Length;
                }
                else if (StartsWith(bytes, Encoding.BigEndianUnicode.GetPreamble()))
                {
                    encoding = new UnicodeEncoding(
                        bigEndian: true,
                        byteOrderMark: false,
                        throwOnInvalidBytes: true
                    );
                    preambleLength = Encoding.BigEndianUnicode.GetPreamble().Length;
                }

                byte[] preamble = new byte[preambleLength];
                Array.Copy(bytes, preamble, preambleLength);
                if (preambleLength == 0 && 0 <= Array.IndexOf(bytes, (byte)0))
                {
                    throw new DecoderFallbackException(
                        "A BOM-less source contains null bytes and cannot be decoded safely."
                    );
                }
                string text = encoding.GetString(
                    bytes,
                    preambleLength,
                    bytes.Length - preambleLength
                );
                return new EncodedSource(text, encoding, preamble);
            }

            public byte[] Encode(string text)
            {
                byte[] content = _encoding.GetBytes(text);
                byte[] bytes = new byte[_preamble.Length + content.Length];
                Array.Copy(_preamble, bytes, _preamble.Length);
                Array.Copy(content, 0, bytes, _preamble.Length, content.Length);
                return bytes;
            }

            private static bool StartsWith(byte[] bytes, byte[] prefix)
            {
                if (prefix.Length == 0 || bytes.Length < prefix.Length)
                {
                    return false;
                }
                for (int index = 0; index < prefix.Length; index++)
                {
                    if (bytes[index] != prefix[index])
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }
#endif
}
