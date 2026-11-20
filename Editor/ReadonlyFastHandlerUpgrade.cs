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
            @"^(?:(?:this|base)\s*\.\s*)?(?<name>[A-Za-z_]\w*)$",
            RegexOptions.CultureInvariant
        );

        private static readonly Regex NamedIdentifierRegex = new(
            @"^(?:\(\s*(?:(?:global::)?DxMessaging\s*\.\s*Core\s*\.\s*)?MessageHandler\s*\.\s*FastHandler(?:WithContext)?\s*<[^()]+>\s*\)\s*)?(?:(?:this|base)\s*\.\s*)?(?<name>[A-Za-z_]\w*)$",
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
            @"(?<qualified>(?<global>global\s*::\s*)?DxMessaging\s*\.\s*Core\s*\.\s*)?\bMessageRegistrationToken(?:\s*\?)?"
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

        private const string TypeArgumentListPattern =
            @"(?:\s*<[\w\p{Nl}\p{Mc}\p{Cf}\s.,:<>?*\[\]()@\\]+>)?";

        private const string CastTypeNamePattern =
            IdentifierSpellingPattern + TypeArgumentListPattern;

        private const string CastTypeSuffixPattern =
            @"(?:\s*[?*]|\s*\[[,\s]*\])*(?:\s+(?<elementName>"
            + IdentifierSpellingPattern
            + @"))?\s*\z";

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

        private static readonly Regex TypeDeclarationRegex = new(
            @"(?<![\w@])(?:class|struct|record(?:\s+(?:class|struct))?)\s+(?<name>"
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
                string suffix =
                    manualReview.Count == 0
                        ? string.Empty
                        : $"\n\n{manualReview.Count} item(s) require manual review. "
                            + "See the Console for details.";
                EditorUtility.DisplayDialog(
                    "DxMessaging Fast Handler Upgrade",
                    "No 3.x fast handlers or inherited string handlers were found under Assets."
                        + suffix,
                    "OK"
                );
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

            SourceMetadata metadata = new();
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
                    Invocation callbackExpression = new(
                        invocation.Name,
                        callback.Start,
                        callback.Start + callback.Length
                    );
                    AddDelegateExpressionReplacements(
                        masked,
                        callbackExpression,
                        replacementStarts
                    );
                    if (TryGetMethodGroup(masked, callback, out string methodName, out _))
                    {
                        methodGroups.Add((methodName, callback));
                    }
                    else if (
                        TryGetQualifiedMethodGroup(masked, callback, out string qualifiedMethod)
                    )
                    {
                        manualReview.Add(
                            $"{qualifiedMethod} is a qualified callback; inspect its declaration manually"
                        );
                    }
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
                        $"{name} is also used as an interceptor; split the callbacks before changing it"
                    );
                    continue;
                }
                if (unprovenMethodGroups.TryGetValue(group.Key, out string reason))
                {
                    manualReview.Add(reason);
                    continue;
                }
                TextSpan conditionalUse = group
                    .Select(entry => entry.Use)
                    .FirstOrDefault(span =>
                        metadata.UncertainBranches.Exists(branch =>
                            span.Start < branch.Start + branch.Length
                            && branch.Start < span.Start + span.Length
                        )
                    );
                AddMethodDeclarationReplacements(
                    source,
                    masked,
                    metadata,
                    name,
                    use.Start,
                    conditionalUse,
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

                AddDelegateExpressionReplacements(masked, expression, replacementStarts);
                if (TryGetMethodGroup(masked, rightHandSideSpan, out string methodName, out _))
                {
                    methodGroups.Add(
                        (methodName, new TextSpan(fastHandler, statementEnd - fastHandler))
                    );
                }
                else
                {
                    if (
                        TryGetQualifiedMethodGroup(
                            masked,
                            rightHandSideSpan,
                            out string qualifiedMethod
                        )
                    )
                    {
                        manualReview.Add(
                            $"{qualifiedMethod} is a qualified callback; inspect its declaration manually"
                        );
                    }
                }
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
            foreach (ParameterList declaration in FindMethodDeclarations(masked, methodName, true))
            {
                int typeStart = FindContainingTypeStart(masked, declaration.DeclarationStart);
                if (!TypeScopeDerivesFromMessageAwareComponent(masked, typeStart, metadata))
                {
                    if (
                        HasRefParameter(
                            masked,
                            declaration.ParametersStart,
                            declaration.ParametersEnd
                        )
                    )
                    {
                        manualReview.Add(
                            $"{methodName} override is not in a directly declared MessageAwareComponent; inspect it manually"
                        );
                    }
                    continue;
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
                                $"{CallbackLocation(masked, declaration.ParametersStart - 1)} has a RegisterForStringMessages property that hides the inherited setting; review the opt-in manually"
                            );
                        }
                        continue;
                    }
                    if (TypeScopeIsPartial(masked, typeStart))
                    {
                        manualReview.Add(
                            $"{methodName} is declared in a partial MessageAwareComponent; add "
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
                            $"{methodName} shares a line with its MessageAwareComponent declaration; add "
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
            foreach (int replacementStart in replacementStarts)
            {
                if (start <= replacementStart && replacementStart < end)
                {
                    return true;
                }
            }
            return false;
        }

        private enum StringHandlerProperty
        {
            Missing,
            Override,
            ConditionalOverride,
            Collision,
        }

        private static StringHandlerProperty FindStringHandlerProperty(
            string masked,
            int typeStart,
            SourceMetadata metadata
        )
        {
            Regex memberRegex = new(
                @"(?<![\w@])(?:(?<modifier>private|protected|internal|public|new|override|virtual|abstract|sealed|static|readonly|required|unsafe|extern|partial)\s+)*"
                    + SimpleDeclaredTypePattern
                    + @"\s+(?<name>"
                    + IdentifierSpellingPattern
                    + @")\s*(?=\{|=>)",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            );
            int typeEnd = FindMatching(masked, typeStart, '{', '}');
            if (typeEnd < 0)
            {
                return StringHandlerProperty.Missing;
            }
            StringHandlerProperty setting = StringHandlerProperty.Missing;
            for (
                Match match = memberRegex.Match(masked, typeStart + 1, typeEnd - typeStart - 1);
                match.Success;
                match = match.NextMatch()
            )
            {
                if (
                    IdentifierValue(match.Groups["name"].Value) != "RegisterForStringMessages"
                    || !IsDirectTypeMember(masked, typeStart, match.Index)
                )
                {
                    continue;
                }
                if (
                    !match
                        .Groups["modifier"]
                        .Captures.Cast<Capture>()
                        .Any(modifier => modifier.Value == "override")
                )
                {
                    return StringHandlerProperty.Collision;
                }
                bool conditional = metadata.UncertainBranches.Exists(branch =>
                    branch.Start < match.Index + match.Length
                    && match.Index < branch.Start + branch.Length
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
            TextSpan conditionalUse,
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
                        $"{methodName} is not declared in this file; inspect its delegate or partial declaration"
                    );
                }
                return;
            }
            if (declarations.Count != 1 || byRefDeclarations.Count != 1)
            {
                manualReview.Add($"{methodName} has overloads or multiple declarations");
                return;
            }

            if (conditionalUse.Length != 0)
            {
                manualReview.Add(
                    $"{CallbackLocation(masked, conditionalUse.Start)} uses '{methodName}' with a conditional callback binding; inspect it in each build configuration"
                );
                return;
            }

            ParameterList declaration = byRefDeclarations[0];
            if (
                HasDirectWritableCall(
                    masked,
                    metadata,
                    metadata.CallableUses(source, masked, methodName),
                    usageScope,
                    declaration
                )
            )
            {
                metadata.NamedCallbacksWithDirectWritableCalls.Add(declaration.ParametersStart - 1);
            }
            AddRefReplacements(
                masked,
                declaration.ParametersStart,
                declaration.ParametersEnd,
                replacementStarts
            );
        }

        private static bool HasDirectWritableCall(
            string masked,
            SourceMetadata metadata,
            IEnumerable<(string Code, Match Name, int Offset)> callableUses,
            int typeStart,
            ParameterList declaration
        )
        {
            bool isTypeMember =
                typeStart < 0
                || IsDirectTypeMember(masked, typeStart, declaration.DeclarationStart);
            foreach ((string code, Match use, int offset) in callableUses)
            {
                int sourceStart = use.Index + offset;
                if (sourceStart == declaration.DeclarationStart)
                {
                    continue;
                }
                int previous = PreviousNonWhitespace(code, use.Index - 1);
                if (
                    FindContainingTypeStart(masked, sourceStart) != typeStart
                    || (
                        0 <= previous
                        && code[previous] == '.'
                        && (
                            !isTypeMember
                            || HasIndependentCallReceiver(
                                masked,
                                metadata,
                                code,
                                previous,
                                typeStart
                            )
                        )
                    )
                )
                {
                    continue;
                }
                int open = NextNonWhitespace(code, use.Index + use.Length);
                int close = FindMatching(code, open, '(', ')');
                if (
                    0 <= close
                    && SplitArguments(code, open + 1, close)
                        .Exists(argument =>
                            0
                            <= FindBaseArgumentRefStart(code, GetArgumentValueSpan(code, argument))
                        )
                )
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasIndependentCallReceiver(
            string masked,
            SourceMetadata metadata,
            string code,
            int dot,
            int typeStart
        )
        {
            int receiverEnd = PreviousNonWhitespace(code, dot - 1);
            if (FollowsKeyword(code, 0, receiverEnd, "base"))
            {
                return true;
            }
            int receiverStart = FindIdentifierStart(code, receiverEnd);
            if (receiverStart < 0 || !IsRootTypeQualifier(code, receiverStart))
            {
                return false;
            }
            string receiver = IdentifierValue(
                code.Substring(receiverStart, receiverEnd - receiverStart + 1)
            );
            bool foundDeclaration = false;
            foreach (Match declaration in metadata.ReceiverDeclarations(masked, receiver))
            {
                if (FindContainingTypeStart(masked, declaration.Index) != typeStart)
                {
                    continue;
                }
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
                        != FindEnclosingBrace(masked, typeStart)
                    || TypeScopeIsPartial(masked, body)
                    || metadata.UncertainBranches.Exists(branch =>
                        branch.Start <= independentType.Index
                        && independentType.Index < branch.Start + branch.Length
                    )
                )
                {
                    return false;
                }
                foundDeclaration = true;
            }
            return foundDeclaration;
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

        private static bool TypeScopeDerivesFromMessageAwareComponent(
            string masked,
            int typeScopeStart,
            SourceMetadata metadata
        )
        {
            if (typeScopeStart < 0)
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
                string header = masked.Substring(match.Index, typeScopeStart - match.Index);
                Match qualifiedBase = Regex.Match(
                    header,
                    @"(?<global>global\s*::\s*)?DxMessaging\s*\.\s*Unity\s*\.\s*MessageAwareComponent\b",
                    RegexOptions.CultureInvariant
                );
                if (qualifiedBase.Success && IsRootTypeQualifier(header, qualifiedBase.Index))
                {
                    return qualifiedBase.Groups["global"].Success
                        || !metadata.HasDxMessagingAlias(masked);
                }
                Match unqualifiedBase = Regex.Match(
                    header,
                    @"\bMessageAwareComponent\b",
                    RegexOptions.CultureInvariant
                );
                return HasNamespaceImport(
                        masked,
                        "DxMessaging.Unity",
                        metadata.HasConditionalDirectives
                    )
                    && !HasTypeOrAliasDeclaration(masked, "MessageAwareComponent")
                    && unqualifiedBase.Success
                    && IsRootTypeQualifier(header, unqualifiedBase.Index);
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
            Match namespaceDeclaration = Regex.Match(
                masked,
                @"\bnamespace\s+[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*\s*(?:[;{])",
                RegexOptions.CultureInvariant
            );
            return !namespaceDeclaration.Success || import.Index < namespaceDeclaration.Index;
        }

        private static bool HasTypeOrAliasDeclaration(string masked, string typeName)
        {
            return Regex.IsMatch(
                    masked,
                    @"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+"
                        + Regex.Escape(typeName)
                        + @"\b|\bdelegate\s+[A-Za-z_]\w*(?:\s*<[^;={}()]+>)?\s+"
                        + Regex.Escape(typeName)
                        + @"\b",
                    RegexOptions.CultureInvariant
                ) || ContainsNamespaceAlias(masked, typeName);
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
            if (0 <= qualifierDot && masked[qualifierDot] == '.')
            {
                int qualifier = PreviousNonWhitespace(masked, qualifierDot - 1);
                if (
                    !FollowsKeyword(masked, 0, qualifier, "this")
                    && !(receiver == "Token" && FollowsKeyword(masked, 0, qualifier, "base"))
                )
                {
                    return false;
                }
            }
            int usageScope = FindContainingTypeStart(masked, methodNameStart);
            IEnumerable<Match> declarations = metadata.ReceiverDeclarations(masked, receiver);
            if (receiver == "Token")
            {
                return TypeScopeDerivesFromMessageAwareComponent(masked, usageScope, metadata)
                    && !declarations.Any(match =>
                        FindContainingTypeStart(masked, match.Index) == usageScope
                    );
            }

            if (
                declarations.Any(match =>
                    match.Groups["type"].Value != "MessageRegistrationToken"
                    && FindContainingTypeStart(masked, match.Index) == usageScope
                )
            )
            {
                return false;
            }
            bool hasDeclaration = false;
            foreach (Match match in metadata.PackageReceiverDeclarations(masked, receiver))
            {
                if (FindContainingTypeStart(masked, match.Index) != usageScope)
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
                            match.Groups["global"].Success || !metadata.HasDxMessagingAlias(masked)
                        );
                }
                else
                {
                    int typePrefix = PreviousNonWhitespace(masked, match.Index - 1);
                    isProven =
                        (typePrefix < 0 || masked[typePrefix] != '.' && masked[typePrefix] != ':')
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
                hasDeclaration = true;
            }
            return hasDeclaration;
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
                if (0 <= cursor && masked[cursor] == '<')
                {
                    cursor = FindMatching(masked, cursor, '<', '>');
                    cursor = cursor < 0 ? -1 : NextNonWhitespace(masked, cursor + 1);
                }
                if (cursor < 0 || masked[cursor] != '(')
                {
                    continue;
                }

                int close = FindMatching(masked, cursor, '(', ')');
                if (0 <= close)
                {
                    Invocation invocation = new(name, cursor + 1, close);
                    if (
                        requireProvenReceiver
                        && !HasProvenTokenReceiver(masked, match.Index, metadata)
                    )
                    {
                        string reason =
                            $"{CallbackLocation(masked, match.Index)} {name} has a receiver whose MessageRegistrationToken type cannot be proven";
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

        private static bool TryGetMethodGroup(
            string masked,
            TextSpan argument,
            out string methodName,
            out string argumentName
        )
        {
            string value = masked.Substring(argument.Start, argument.Length).Trim();
            Match match = NamedIdentifierRegex.Match(value);
            if (!match.Success)
            {
                match = ConstructedHandlerRegex.Match(value);
            }
            if (!match.Success && 2 <= value.Length && value[0] == '(' && value[^1] == ')')
            {
                match = IdentifierRegex.Match(value.Substring(1, value.Length - 2).Trim());
            }
            methodName = match.Success ? match.Groups["name"].Value : string.Empty;
            argumentName = string.Empty;
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
                )
                && !Regex.IsMatch(
                    expression,
                    @"^(?:this|base)\s*\.",
                    RegexOptions.CultureInvariant
                );
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

        private static void AddDelegateExpressionReplacements(
            string masked,
            Invocation invocation,
            HashSet<int> replacementStarts
        )
        {
            if (TryGetInlineDelegateParameters(masked, invocation, out TextSpan parameters))
            {
                AddRefReplacements(
                    masked,
                    parameters.Start,
                    parameters.Start + parameters.Length,
                    replacementStarts
                );
            }
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

            // A deferred callback can remove a base argument used by another proposed edit.
            // Recheck the remaining callbacks each time a shared argument is removed.
            bool removedBaseForwarding;
            do
            {
                removedBaseForwarding = false;
                foreach (
                    KeyValuePair<int, List<int>> entry in parameterReplacementsByOpen.Where(entry =>
                        replacementStarts.Contains(entry.Value[0])
                    )
                )
                {
                    removedBaseForwarding |= PruneConditionalCallback(
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
            } while (removedBaseForwarding);
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
                return false;
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
            if (
                reason == null
                && metadata.NamedCallbacksWithDirectWritableCalls.Contains(entry.Key)
            )
            {
                reason = "has a direct call that depends on its 'ref' parameter";
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

        private static bool IsGenericOpenAngle(string text, int openAngle, int end)
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

        private static string CallbackLocation(string text, int position)
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
            return $"callback on line {line}, column {position - lineStart + 1}";
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

        private sealed class SourceMetadata
        {
            public readonly List<TextSpan> InterpolatedExpressions = new();
            public readonly List<TextSpan> UncertainBranches = new();
            public readonly List<int> ConditionalDirectives = new();
            public readonly HashSet<int> NamedCallbacksWithDirectWritableCalls = new();

            private bool? _hasDxMessagingAlias;
            private ILookup<string, Match> _receiverDeclarations;
            private ILookup<string, Match> _packageReceiverDeclarations;
            private ILookup<string, (string Code, Match Name, int Offset)> _callableUses;

            public bool HasConditionalDirectives => 0 < ConditionalDirectives.Count;

            public bool HasDxMessagingAlias(string masked)
            {
                _hasDxMessagingAlias ??= ContainsNamespaceAlias(masked, "DxMessaging");
                return _hasDxMessagingAlias.Value;
            }

            public IEnumerable<Match> ReceiverDeclarations(string masked, string receiver)
            {
                _receiverDeclarations ??= IndexReceiverDeclarations(
                    ReceiverDeclarationRegex,
                    masked
                );
                return _receiverDeclarations[receiver];
            }

            public IEnumerable<Match> PackageReceiverDeclarations(string masked, string receiver)
            {
                _packageReceiverDeclarations ??= IndexReceiverDeclarations(
                    PackageReceiverDeclarationRegex,
                    masked
                );
                return _packageReceiverDeclarations[receiver];
            }

            public IEnumerable<(string Code, Match Name, int Offset)> CallableUses(
                string source,
                string masked,
                string name
            )
            {
                _callableUses ??= FindCallableUses(masked, 0)
                    .Concat(
                        InterpolatedExpressions.SelectMany(expression =>
                            FindCallableUses(
                                MaskNonCode(
                                    source.Substring(expression.Start, expression.Length),
                                    null
                                ),
                                expression.Start
                            )
                        )
                    )
                    .ToLookup(use => IdentifierValue(use.Name.Value), StringComparer.Ordinal);
                return _callableUses[name];
            }

            private static IEnumerable<(string Code, Match Name, int Offset)> FindCallableUses(
                string code,
                int offset
            )
            {
                foreach (Match use in IdentifierUseRegex.Matches(code))
                {
                    int next = NextNonWhitespace(code, use.Index + use.Length);
                    if (0 <= next && code[next] == '(')
                    {
                        yield return (code, use, offset);
                    }
                }
            }

            private static ILookup<string, Match> IndexReceiverDeclarations(
                Regex pattern,
                string masked
            )
            {
                return pattern
                    .Matches(masked)
                    .Cast<Match>()
                    .SelectMany(declaration =>
                        ReceiverNames(masked, declaration)
                            .Select(name => (Name: name, Declaration: declaration))
                    )
                    .ToLookup(
                        entry => entry.Name,
                        entry => entry.Declaration,
                        StringComparer.Ordinal
                    );
            }

            private static IEnumerable<string> ReceiverNames(string masked, Match declaration)
            {
                Group name = declaration.Groups["name"];
                yield return IdentifierValue(name.Value);
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
                    yield return IdentifierValue(additional.Value);
                }
            }
        }

        private sealed class ConditionalMask
        {
            private readonly Stack<(int Start, BranchState Outer, BranchState Remaining)> _scopes =
                new();
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
                if (keyword != "if")
                {
                    if (_scopes.Count == 0)
                    {
                        _uncertainTail = Math.Min(_uncertainTail, start);
                        return;
                    }
                    (int Start, BranchState Outer, BranchState Remaining) scope = _scopes.Pop();
                    outer = scope.Outer;
                    remaining = scope.Remaining;
                    RecordBranch(scope.Start, start);
                }

                if (keyword == "endif")
                {
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
                _scopes.Push((start, outer, Intersect(remaining, unselected)));
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
                    (int start, BranchState outer, _) = _scopes.Pop();
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
            int identifierStart = identifierEnd;
            while (0 <= identifierStart && IsIdentifierPart(text[identifierStart]))
            {
                identifierStart--;
            }
            identifierStart++;
            bool correctIdentifier =
                identifierStart <= identifierEnd
                && string.CompareOrdinal(
                    text,
                    identifierStart,
                    "MessageHandler",
                    0,
                    "MessageHandler".Length
                ) == 0
                && identifierEnd - identifierStart + 1 == "MessageHandler".Length;
            if (!correctIdentifier)
            {
                return false;
            }
            hasHandlerSyntax = true;

            if (
                Regex.IsMatch(
                    text,
                    @"\b(?:class|struct|record(?:\s+(?:class|struct))?)\s+MessageHandler\b|\busing\s+MessageHandler\s*=",
                    RegexOptions.CultureInvariant
                )
            )
            {
                return false;
            }

            if (HasDxMessagingCoreQualifier(text, identifierStart, out bool globallyQualified))
            {
                isProven = globallyQualified || !metadata.HasDxMessagingAlias(text);
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
            isProven = !metadata.HasConditionalDirectives;
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
            int coreStart = coreEnd;
            while (0 <= coreStart && IsIdentifierPart(text[coreStart]))
            {
                coreStart--;
            }
            coreStart++;
            if (
                coreEnd < coreStart
                || string.CompareOrdinal(text, coreStart, "Core", 0, "Core".Length) != 0
                || coreEnd - coreStart + 1 != "Core".Length
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
            int dxMessagingStart = dxMessagingEnd;
            while (0 <= dxMessagingStart && IsIdentifierPart(text[dxMessagingStart]))
            {
                dxMessagingStart--;
            }
            dxMessagingStart++;
            int qualifierPrefix = PreviousNonWhitespace(text, dxMessagingStart - 1);
            globallyQualified =
                0 <= qualifierPrefix
                && text[qualifierPrefix] == ':'
                && Regex.IsMatch(
                    text.Substring(0, dxMessagingStart),
                    @"(?<![\w@\p{Nl}\p{Mc}\p{Cf}])global\s*::\s*$",
                    RegexOptions.CultureInvariant
                );
            bool rootQualified =
                qualifierPrefix < 0
                || text[qualifierPrefix] != '.' && text[qualifierPrefix] != ':'
                || globallyQualified;
            return rootQualified
                && dxMessagingStart <= dxMessagingEnd
                && string.CompareOrdinal(
                    text,
                    dxMessagingStart,
                    "DxMessaging",
                    0,
                    "DxMessaging".Length
                ) == 0
                && dxMessagingEnd - dxMessagingStart + 1 == "DxMessaging".Length;
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
