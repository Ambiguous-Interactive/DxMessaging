namespace DxMessaging.Editor
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using UnityEditor;
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

        private static readonly Regex RegistrationNameRegex = new(
            @"\b(Register[A-Za-z0-9_]+)\b",
            RegexOptions.CultureInvariant
        );

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

        [MenuItem(MenuPath)]
        private static void UpgradeProject()
        {
            string[] guids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });
            List<FileUpgrade> upgrades = new();
            SortedSet<string> manualReview = new(StringComparer.Ordinal);

            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (
                    !assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || IsGeneratedPath(assetPath)
                )
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(assetPath);
                byte[] originalBytes = File.ReadAllBytes(fullPath);
                byte[] upgradedBytes;
                UpgradeResult result;
                try
                {
                    EncodedSource source = EncodedSource.FromBytes(originalBytes);
                    if (IsGeneratedSource(assetPath, source.Text))
                    {
                        continue;
                    }
                    result = Analyze(source.Text);
                    upgradedBytes = source.Encode(result.UpgradedSource);
                }
                catch (DecoderFallbackException)
                {
                    manualReview.Add(
                        $"{assetPath}: encoding is not UTF-8, UTF-16, or UTF-32; file was not read"
                    );
                    continue;
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

                if (0 < result.ReplacementCount || 0 < result.StringHandlerOptInCount)
                {
                    upgrades.Add(
                        new FileUpgrade(
                            fullPath,
                            originalBytes,
                            upgradedBytes,
                            result.ReplacementCount,
                            result.StringHandlerOptInCount
                        )
                    );
                }
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
                    + "Only scripts under Assets are changed. Interceptors and emission calls remain "
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
                ApplyAtomically(upgrades);
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
                AssetDatabase.Refresh();
                Debug.Log(
                    $"[DxMessaging] Upgraded {replacementCount} fast-handler parameter(s) and "
                        + $"added {stringHandlerOptInCount} string-handler opt-in(s) in "
                        + $"{upgrades.Count} script(s). Review the diff before committing."
                );
                LogManualReview(manualReview);
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

        internal static UpgradeResult Analyze(string source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            SourceMetadata metadata = new(source);
            string masked = MaskNonCode(source, metadata.InterpolatedExpressions, metadata);
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
                    source,
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
            int searchStart = 0;
            while (searchStart < masked.Length)
            {
                int fastHandler = masked.IndexOf(
                    "FastHandler",
                    searchStart,
                    StringComparison.Ordinal
                );
                if (fastHandler < 0)
                {
                    return;
                }
                searchStart = fastHandler + "FastHandler".Length;

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

                int statementEnd = FindStatementEnd(masked, fastHandler);
                if (statementEnd < 0)
                {
                    continue;
                }

                int equals = masked.IndexOf('=', fastHandler, statementEnd - fastHandler);
                if (
                    equals < 0
                    || equals + 1 < masked.Length && masked[equals + 1] == '>'
                    || !LooksLikeFastDelegateVariableDeclaration(masked, fastHandler, equals)
                )
                {
                    continue;
                }

                Invocation expression = new("FastHandler", equals + 1, statementEnd);
                TextSpan rightHandSideSpan = new(equals + 1, statementEnd - equals - 1);
                if (!isProven)
                {
                    string reason =
                        $"{CallbackLocation(masked, fastHandler)} uses a delegate whose MessageHandler type cannot be proven; inspect its binding manually";
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
                        : MethodGroupMayNeedMigration(masked, rightHandSideSpan);
                    if (needsReview)
                    {
                        manualReview.Add(reason);
                    }
                    continue;
                }

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

        private static bool LooksLikeFastDelegateVariableDeclaration(
            string masked,
            int fastHandlerStart,
            int equals
        )
        {
            int genericOpen = masked.IndexOf('<', fastHandlerStart);
            int genericClose = FindMatching(masked, genericOpen, '<', '>');
            if (genericClose < 0 || equals <= genericClose)
            {
                return false;
            }
            string declarator = masked.Substring(genericClose + 1, equals - genericClose - 1);
            return Regex.IsMatch(
                declarator,
                @"^\s+[A-Za-z_]\w*\s*$",
                RegexOptions.CultureInvariant
            );
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
                    manualReview.Add(
                        $"{CallbackLocation(masked, declaration.DeclarationStart)} {methodName} override is not in a directly declared MessageAwareComponent; inspect it manually"
                    );
                    continue;
                }
                string useIssue = FindRelatedOverrideIssue(masked, typeStart, declarations)
                    ?? FindNamedCallbackUseIssue(
                        source,
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
            string source,
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
                source,
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
            string source,
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
                (string code, Match use, int offset) in metadata.IdentifierUses(
                    source,
                    masked,
                    methodName
                )
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
                        if (
                            !SplitArguments(code, open + 1, close)
                                .Exists(argument =>
                                    0
                                    <= FindBaseArgumentRefStart(
                                        code,
                                        GetArgumentValueSpan(code, argument)
                                    )
                                )
                        )
                        {
                            continue;
                        }
                        return $"has a direct call at {SourceLocation(masked, sourceStart)} that depends on its 'ref' parameter";
                    }
                }

                bool conditional = metadata.UncertainBranches.Exists(branch =>
                    branch.Start <= sourceStart && sourceStart < branch.Start + branch.Length
                );
                string binding = conditional ? "conditional callback binding" : "callback binding";
                return $"has a {binding} at {SourceLocation(masked, sourceStart)} that cannot be proven safe for 'in'";
            }
            return null;
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
                if (directBase.Groups["qualified"].Success)
                {
                    return directBase.Groups["global"].Success
                        || !metadata.HasDxMessagingShadow(masked);
                }
                return HasNamespaceImport(
                        masked,
                        "DxMessaging.Unity",
                        metadata.HasConditionalDirectives
                    ) && !HasTypeOrAliasDeclaration(masked, "MessageAwareComponent");
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
                Group qualified = match.Groups["qualified"];
                bool isProven;
                if (qualified.Success)
                {
                    isProven =
                        IsRootTypeQualifier(masked, qualified.Index)
                        && (
                            match.Groups["global"].Success || !metadata.HasDxMessagingShadow(masked)
                        );
                }
                else
                {
                    int typePrefix = PreviousNonWhitespace(masked, match.Index - 1);
                    isProven =
                        (typePrefix < 0 || masked[typePrefix] is not ('.' or ':' or '@'))
                        && HasNamespaceImport(
                            masked,
                            "DxMessaging.Core",
                            metadata.HasConditionalDirectives
                        )
                        && !HasTypeOrAliasDeclaration(masked, "MessageRegistrationToken");
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

        private static bool InvocationMayNeedMigration(string masked, Invocation invocation)
        {
            foreach (TextSpan callback in GetCallbackArguments(masked, invocation))
            {
                string expression = masked.Substring(callback.Start, callback.Length);
                if (RefRegex.IsMatch(expression))
                {
                    return true;
                }
                if (MethodGroupMayNeedMigration(masked, callback))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MethodGroupMayNeedMigration(string masked, TextSpan callback)
        {
            return TryGetMethodGroup(masked, callback, out string methodName, out _)
                && FindMethodDeclarations(masked, methodName, requireOverride: false)
                    .Exists(declaration =>
                        HasRefParameter(
                            masked,
                            declaration.ParametersStart,
                            declaration.ParametersEnd
                        )
                    );
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
            foreach (Match match in RegistrationNameRegex.Matches(masked))
            {
                string name = match.Groups[1].Value;
                if (!acceptedNames.Contains(name))
                {
                    continue;
                }

                int cursor = NextNonWhitespace(masked, match.Index + match.Length);
                List<TextSpan> typeArguments = new();
                if (0 <= cursor && masked[cursor] == '<')
                {
                    int genericClose = FindMatching(masked, cursor, '<', '>');
                    if (genericClose < 0)
                    {
                        continue;
                    }
                    typeArguments = SplitParameters(masked, cursor + 1, genericClose);
                    cursor = NextNonWhitespace(masked, genericClose + 1);
                }
                if (cursor < 0 || masked[cursor] != '(')
                {
                    continue;
                }

                int close = FindMatching(masked, cursor, '(', ')');
                if (0 <= close)
                {
                    Invocation invocation = new(name, cursor + 1, close);
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
                        string reason =
                            $"{CallbackLocation(masked, match.Index)} {name} {bindingIssue}";
                        foreach (string group in FindCallbackMethodGroupKeys(masked, invocation))
                        {
                            unprovenMethodGroups?.TryAdd(group, reason);
                        }
                        if (manualReview != null && InvocationMayNeedMigration(masked, invocation))
                        {
                            manualReview.Add(reason);
                        }
                        continue;
                    }
                    invocations.Add(invocation);
                }
            }
            return invocations;
        }

        private static bool MatchesPackageRegistration(
            string masked,
            SourceMetadata metadata,
            Invocation invocation,
            List<TextSpan> typeArguments
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
            if (HasExplicitPackageDelegate(masked, metadata, callback))
            {
                return true;
            }
            string messageType;
            if (typeArguments.Count == 1)
            {
                TextSpan typeArgument = typeArguments[0];
                messageType = CompactType(
                    masked.Substring(typeArgument.Start, typeArgument.Length)
                );
            }
            else
            {
                int open = FindCallbackParametersOpen(masked, callback);
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

            return HasProvenRouteMessage(
                    masked,
                    metadata,
                    messageType,
                    route,
                    invocation.Start,
                    typeScope
                )
                && HasProvenCallbackParameters(
                    masked,
                    metadata,
                    callback,
                    messageType,
                    name.Contains("WithoutSource") || name.Contains("WithoutTargeting")
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
            int open = FindCallbackParametersOpen(masked, callback);
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
                    ? !IsKnownType(masked, metadata, actualMessage, messageType)
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
                    "DxMessaging.Core.InstanceId"
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

        private static bool HasExplicitPackageDelegate(
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
            int prefixEnd;
            if (TryGetInlineDelegateParameters(masked, expression, out TextSpan parameters))
            {
                prefixEnd = parameters.Start - 1;
            }
            else if (!TryGetMethodGroup(masked, callback, out _, out prefixEnd))
            {
                return false;
            }

            int handler = masked.IndexOf(
                "FastHandler",
                callback.Start,
                prefixEnd - callback.Start,
                StringComparison.Ordinal
            );
            return 0 <= handler
                && TryClassifyFastDelegateType(masked, handler, metadata, out bool proven, out _)
                && proven;
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
                IsKnownRouteMarkerType(masked, metadata, type, marker)
                || (
                    route == "Untargeted"
                    && IsKnownType(
                        masked,
                        metadata,
                        type,
                        "DxMessaging.Core.Messages.GlobalStringMessage"
                    )
                )
                || (
                    route == "Targeted"
                    && IsKnownType(
                        masked,
                        metadata,
                        type,
                        "DxMessaging.Core.Messages.StringMessage"
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
                name.Contains('.') || (0 <= typeScope && TypeScopeIsPartial(masked, typeScope))
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
                || HasPotentialTypeParameter(masked, name, use)
                || HasUnprovenTypeScope(masked, metadata, typeScope, declarationScope)
            )
            {
                return false;
            }

            int headerStart = match.Index + match.Length;
            int body = masked.IndexOf('{', headerStart);
            int colon = NextNonWhitespace(masked, headerStart);
            if (
                body < 0
                || colon < 0
                || body <= colon
                || masked[colon] != ':'
                || metadata.UncertainBranches.Exists(branch =>
                    branch.Start < body && match.Index < branch.Start + branch.Length
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

        private static bool HasPotentialTypeParameter(string masked, string name, int use)
        {
            for (
                int body = FindEnclosingBrace(masked, use);
                0 <= body;
                body = FindEnclosingBrace(masked, body)
            )
            {
                int start = masked.LastIndexOfAny(new[] { '{', '}', ';' }, body - 1) + 1;
                string header = masked.Substring(start, body - start);
                if (
                    Regex
                        .Matches(
                            header,
                            "<(?<name>[^<>]+)>",
                            RegexOptions.CultureInvariant,
                            TimeSpan.FromSeconds(1)
                        )
                        .Cast<Match>()
                        .Any(list =>
                            list.Groups["name"].Value.Split(',').Any(token => token.Trim() == name)
                        )
                )
                {
                    return true;
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
                .Any(type => IsRouteMarker(header, metadata, type, marker, masked));
        }

        private static bool IsRouteMarker(
            string text,
            SourceMetadata metadata,
            TextSpan span,
            string marker,
            string source = null
        )
        {
            string name = CompactType(text.Substring(span.Start, span.Length));
            return IsKnownRouteMarkerType(source ?? text, metadata, name, marker);
        }

        private static bool IsKnownRouteMarkerType(
            string masked,
            SourceMetadata metadata,
            string spelling,
            string marker
        )
        {
            int generic = spelling.IndexOf('<');
            if (generic < 0)
            {
                return IsKnownType(masked, metadata, spelling, marker);
            }
            return FindMatching(spelling, generic, '<', '>') == spelling.Length - 1
                && ReadTypeArity(spelling, generic) == 1
                && IsKnownType(masked, metadata, spelling.Substring(0, generic), marker, arity: 1);
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
                    parts.Add(
                        string.Join(
                            ".",
                            IdentifierUseRegex
                                .Matches(declaration.Groups["path"].Value)
                                .Cast<Match>()
                                .Select(part => IdentifierValue(part.Value))
                        )
                    );
                }
            }
            return string.Join(".", parts);
        }

        private static string CompactType(string text) =>
            new string(text.Where(character => !char.IsWhiteSpace(character)).ToArray());

        private static bool IsKnownType(
            string masked,
            SourceMetadata metadata,
            string spelling,
            string qualifiedName,
            int arity = 0
        )
        {
            if (metadata.DeclaresQualifiedType(masked, qualifiedName, arity))
            {
                return false;
            }
            if (spelling == "global::" + qualifiedName)
            {
                return true;
            }
            if (spelling == qualifiedName)
            {
                return !metadata.HasNamespaceShadow(
                    masked,
                    qualifiedName.Substring(0, qualifiedName.IndexOf('.'))
                );
            }
            int dot = qualifiedName.LastIndexOf('.');
            string name = qualifiedName.Substring(dot + 1);
            return spelling == name
                && HasNamespaceImport(
                    masked,
                    qualifiedName.Substring(0, dot),
                    metadata.HasConditionalDirectives
                )
                && !HasTypeOrAliasDeclaration(masked, name);
        }

        private static bool HasProvenRegistrationValue(
            string masked,
            SourceMetadata metadata,
            TextSpan value,
            int typeScope,
            string requiredType
        )
        {
            string expression = masked.Substring(value.Start, value.Length).Trim();
            while (
                expression.Length != 0
                && expression[0] == '('
                && FindMatching(expression, 0, '(', ')') == expression.Length - 1
            )
            {
                expression = expression.Substring(1, expression.Length - 2).Trim();
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
                return CanPassRegistrationValue(masked, metadata, explicitType, requiredType);
            }
            if (TypeScopeDerivesFromMessageAwareComponent(masked, typeScope, metadata))
            {
                if (expression == "this")
                {
                    return CanPassRegistrationValue(
                        masked,
                        metadata,
                        "global::UnityEngine.Component",
                        requiredType
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
                        requiredType
                    );
                }
            }

            Match identifier = IdentifierUseRegex.Match(expression);
            if (
                !identifier.Success
                || identifier.Index != 0
                || identifier.Length != expression.Length
            )
            {
                return false;
            }
            string variable = IdentifierValue(identifier.Value);
            int valueStart = NextNonWhitespace(masked, value.Start);
            var declarations = metadata
                .ReceiverDeclarations(masked, variable)
                .Where(entry =>
                    entry.NameStart != valueStart
                    && FindContainingTypeStart(masked, entry.NameStart) == typeScope
                )
                .ToArray();
            if (
                metadata.HasUnclassifiedReceiverUse(
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
                        requiredType
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
            string requiredType
        )
        {
            if (requiredType == "System.Int32" && spelling == "int")
            {
                return true;
            }
            return IsKnownType(masked, metadata, spelling, requiredType)
                || (
                    requiredType == "DxMessaging.Core.InstanceId"
                    && (
                        IsKnownType(masked, metadata, spelling, "UnityEngine.GameObject")
                        || IsKnownType(masked, metadata, spelling, "UnityEngine.Component")
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
                int prefixEnd = inline ? parameters.Start - 1 : nameStart;
                int explicitDelegate = masked.IndexOf(
                    "FastHandler",
                    callback.Start,
                    prefixEnd - callback.Start,
                    StringComparison.Ordinal
                );
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
            int valueStart = NextNonWhitespace(masked, argument.Start);
            Match match = NamedIdentifierRegex.Match(value);
            if (!match.Success)
            {
                match = ConstructedHandlerRegex.Match(value);
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

        private static bool IsDirectDelegatePrefix(string text, int start, int parameterStart)
        {
            string prefix = text.Substring(start, parameterStart - start).Trim();
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
            List<(string Parameter, TextSpan Declaration)> localShadows =
                CollectLocalFunctionShadows(masked, body, metadata);
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
                        samePayload
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

            var globalRegistrations = readonlyInvocations
                .Where(invocation => invocation.Name == "RegisterGlobalAcceptAll")
                .Select(invocation =>
                    (
                        invocation.Start,
                        Callbacks: GetCallbackArguments(masked, invocation)
                            .Select(callback => FindCallbackParametersOpen(masked, callback))
                            .ToArray()
                    )
                )
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
                foreach ((int start, int[] callbacks) in globalRegistrations)
                {
                    int[] remaining = callbacks
                        .Where(open =>
                            parameterReplacementsByOpen.TryGetValue(open, out List<int> edits)
                            && replacementStarts.Contains(edits[0])
                        )
                        .ToArray();
                    if (remaining.Length == 0 || remaining.Length == callbacks.Length)
                    {
                        continue;
                    }
                    foreach (int open in remaining)
                    {
                        recheck |= metadata.NamedCallbackUseIssues.TryAdd(
                            open,
                            $"shares the RegisterGlobalAcceptAll call at {SourceLocation(masked, start)} with a callback that requires manual review"
                        );
                    }
                }
            } while (recheck);
        }

        private static int FindCallbackParametersOpen(string masked, TextSpan callback)
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
            if (!TryGetMethodGroup(masked, callback, out string name, out int use))
            {
                return -1;
            }
            int typeScope = FindContainingTypeStart(masked, use);
            List<ParameterList> declarations = FindMethodDeclarations(masked, name, false)
                .FindAll(declaration =>
                    FindContainingTypeStart(masked, declaration.DeclarationStart) == typeScope
                );
            return declarations.Count == 1 ? declarations[0].ParametersStart - 1 : -1;
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

            List<(string Parameter, TextSpan Declaration)> localShadows =
                CollectLocalFunctionShadows(masked, body, metadata);

            List<int> forwardingStarts = new();
            int enclosingOverride = -1;
            string reason = null;
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

        private static List<(string Parameter, TextSpan Declaration)> CollectLocalFunctionShadows(
            string masked,
            TextSpan body,
            SourceMetadata metadata
        )
        {
            // Conditional compilation can change the declaration or the end of its scope.
            if (
                metadata.ConditionalDirectives.Exists(position =>
                    body.Start <= position && position < body.Start + body.Length
                )
            )
            {
                return new();
            }
            try
            {
                return FindLocalFunctionShadows(masked, body).ToList();
            }
            catch (RegexMatchTimeoutException exception)
            {
                DxMessagingEditorLog.LogWarning(
                    "Local function scope detection timed out. Scanning all parameter uses.",
                    exception
                );
                return new();
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
                        || FollowsKeyword(text, 0, previous, "as");
                }
                start = FindIdentifierStart(text, end);
            }
            return false;
        }

        private static string CallbackLocation(string text, int position) =>
            $"callback on {SourceLocation(text, position)}";

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
                    // A comparison in a nested expression cannot affect the outer argument list.
                    case '<'
                        when parentheses == 0
                            && brackets == 0
                            && braces == 0
                            && (
                                0 < angles
                                || containsParameterTypes
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
                        conditional.ObserveMaskedToken(source.AsSpan(index, end - index));
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
            int lineEnd = FindDirectiveLineEnd(source.AsSpan(), index);
            int keywordStart = NextNonWhitespace(source, index + 1);
            if (keywordStart < 0 || lineEnd <= keywordStart)
            {
                Mask(masked, index, lineEnd);
                return lineEnd;
            }
            int keywordEnd = keywordStart;
            while (keywordEnd < lineEnd && IsIdentifierPart(source[keywordEnd]))
            {
                keywordEnd++;
            }

            string keyword = source.Substring(keywordStart, keywordEnd - keywordStart);
            if (keyword is "if" or "elif" or "else" or "endif")
            {
                conditional.Visit(keyword, source.AsSpan(keywordEnd, lineEnd - keywordEnd), index);
            }
            Mask(masked, index, lineEnd);
            return lineEnd;
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
            public SourceMetadata(string source)
            {
                Source = source;
            }

            public string Source { get; }

            public readonly List<TextSpan> InterpolatedExpressions = new();
            public readonly List<TextSpan> UncertainBranches = new();
            public readonly List<int> ConditionalDirectives = new();
            public readonly List<ConditionalGroup> CompleteConditionalGroups = new();
            public readonly Dictionary<int, string> NamedCallbackUseIssues = new();

            private HashSet<(string Name, int Arity)> _qualifiedTypeDeclarations;
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

            public bool DeclaresQualifiedType(string masked, string name, int arity = 0)
            {
                if (_qualifiedTypeDeclarations == null)
                {
                    _qualifiedTypeDeclarations = new();
                    foreach (Match declaration in TypeDeclarationRegex.Matches(masked))
                    {
                        if (FindContainingTypeStart(masked, declaration.Index) >= 0)
                        {
                            continue;
                        }
                        string scope = NamespaceAt(masked, declaration.Index);
                        string simpleName = IdentifierValue(declaration.Groups["name"].Value);
                        string qualifiedName =
                            scope.Length == 0 ? simpleName : scope + "." + simpleName;
                        _qualifiedTypeDeclarations.Add(
                            (qualifiedName, DeclarationArity(masked, declaration))
                        );
                    }
                }
                return _qualifiedTypeDeclarations.Contains((name, arity));
            }

            public bool HasDxMessagingShadow(string masked) =>
                HasNamespaceShadow(masked, "DxMessaging");

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

            private IEnumerable<Match> MaskedIdentifierUses(string masked, string name)
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
                    if (IsReceiverReference(masked, use, before, after))
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
                    // A standalone argument list cannot be a lambda or a deconstruction declaration.
                    return 0 <= following && masked[following] == ';';
                }
                return false;
            }

            public IEnumerable<(string Code, Match Name, int Offset)> IdentifierUses(
                string source,
                string masked,
                string name
            )
            {
                _identifierUses ??= FindIdentifierUses(masked, 0)
                    .Concat(
                        InterpolatedExpressions.SelectMany(expression =>
                            FindIdentifierUses(
                                MaskNonCode(
                                    source.Substring(expression.Start, expression.Length),
                                    null
                                ),
                                expression.Start
                            )
                        )
                    )
                    .ToLookup(use => IdentifierValue(use.Name.Value), StringComparer.Ordinal);
                return _identifierUses[name];
            }

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

            public void ObserveMaskedToken(ReadOnlySpan<char> token)
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
                    if (IsDirectiveStart(token, hash))
                    {
                        // A directive-position hash can become active when this token is disabled.
                        _uncertainTail = Math.Min(_uncertainTail, _scopes.Peek().Start);
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
            int typeEnd = fastHandlerStart + "FastHandler".Length;
            if (StartsWithIdentifier(text, typeEnd, "WithContext"))
            {
                typeEnd += "WithContext".Length;
            }
            int genericOpen = NextNonWhitespace(text, typeEnd);
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

            if (HasDxMessagingCoreQualifier(text, identifierStart, out bool globallyQualified))
            {
                isProven =
                    (globallyQualified || !metadata.HasDxMessagingShadow(text))
                    && !metadata.DeclaresQualifiedType(text, "DxMessaging.Core.MessageHandler");
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
            isProven =
                !metadata.HasConditionalDirectives
                && !HasTypeOrAliasDeclaration(text, "MessageHandler");
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

        private static void ApplyAtomically(List<FileUpgrade> upgrades)
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

            ApplyAtomically(pending, new PhysicalAtomicFileStore());
        }

        internal static void ApplyAtomically(
            IReadOnlyList<PendingFileUpgrade> upgrades,
            IAtomicFileStore store
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
            public string Replace(string fullPath, byte[] originalBytes, byte[] upgradedBytes)
            {
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
