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

        private static readonly Regex RefRegex = new(@"\bref\b", RegexOptions.CultureInvariant);

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

        private const string SimpleDeclaredTypePattern =
            IdentifierSpellingPattern
            + @"(?:\s*(?:\.|::)\s*"
            + IdentifierSpellingPattern
            + @")*(?:\s*[?*]|\s*\[[,\s]*\])*";

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
            @"\G(?<grouping>(?:\(\s*)*)(?<name>" + IdentifierSpellingPattern + ")",
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
            @"\b(?:class|struct|record(?:\s+(?:class|struct))?)\s+[A-Za-z_]\w*(?:\s*<[^>{};]+>)?",
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

            List<TextSpan> interpolatedExpressions = new();
            string masked = MaskNonCode(source, interpolatedExpressions);
            interpolatedExpressions.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            HashSet<int> replacementStarts = new();
            SortedSet<string> manualReview = new(StringComparer.Ordinal);
            List<Invocation> readonlyInvocations = FindInvocations(
                masked,
                ReadonlyRegistrationNames,
                manualReview,
                requireProvenReceiver: true
            );
            List<Invocation> mutableInvocations = FindInvocations(
                masked,
                MutableRegistrationNames,
                manualReview: null,
                requireProvenReceiver: false
            );
            HashSet<string> mutableMethodGroups = CollectCallbackMethodGroups(
                masked,
                mutableInvocations
            );

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
                        AddMethodDeclarationReplacements(
                            masked,
                            methodName,
                            callback.Start,
                            mutableMethodGroups,
                            replacementStarts,
                            manualReview
                        );
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
                mutableMethodGroups,
                replacementStarts,
                manualReview
            );

            Dictionary<int, int> baseForwardingOwners = new();
            foreach (string overrideName in ChangedOverrideNames)
            {
                AddOverrideDeclarationReplacements(
                    masked,
                    overrideName,
                    replacementStarts,
                    baseForwardingOwners,
                    manualReview
                );
            }

            PruneIncompatibleParameterUses(
                source,
                masked,
                interpolatedExpressions,
                replacementStarts,
                baseForwardingOwners,
                manualReview
            );

            SortedDictionary<int, string> insertions = new();
            AddStringHandlerOptIns(masked, replacementStarts, insertions, manualReview);

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
            HashSet<string> mutableMethodGroups,
            HashSet<int> replacementStarts,
            SortedSet<string> manualReview
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

                if (!IsMessageHandlerQualified(masked, fastHandler))
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
                AddDelegateExpressionReplacements(masked, expression, replacementStarts);
                TextSpan rightHandSideSpan = new(equals + 1, statementEnd - equals - 1);
                if (TryGetMethodGroup(masked, rightHandSideSpan, out string methodName, out _))
                {
                    AddMethodDeclarationReplacements(
                        masked,
                        methodName,
                        equals + 1,
                        mutableMethodGroups,
                        replacementStarts,
                        manualReview
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

        private static void AddMethodDeclarationReplacements(
            string masked,
            string methodName,
            int usagePosition,
            HashSet<string> mutableMethodGroups,
            HashSet<int> replacementStarts,
            SortedSet<string> manualReview
        )
        {
            string methodKey = ScopedMethodKey(masked, methodName, usagePosition);
            if (mutableMethodGroups.Contains(methodKey))
            {
                manualReview.Add(
                    $"{methodName} is also used as an interceptor; split the callbacks before changing it"
                );
                return;
            }

            AddUniqueDeclarationReplacements(
                masked,
                methodName,
                replacementStarts,
                manualReview,
                requireOverride: false,
                reportMissing: true,
                usagePosition: usagePosition
            );
        }

        private static void AddOverrideDeclarationReplacements(
            string masked,
            string methodName,
            HashSet<int> replacementStarts,
            Dictionary<int, int> baseForwardingOwners,
            SortedSet<string> manualReview
        )
        {
            foreach (ParameterList declaration in FindMethodDeclarations(masked, methodName, true))
            {
                int typeStart = FindContainingTypeStart(masked, declaration.DeclarationStart);
                if (!TypeScopeDerivesFromMessageAwareComponent(masked, typeStart))
                {
                    if (
                        RefRegex.IsMatch(
                            masked.Substring(
                                declaration.ParametersStart,
                                declaration.ParametersEnd - declaration.ParametersStart
                            )
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
                    masked,
                    declaration,
                    methodName,
                    replacementStarts,
                    baseForwardingOwners
                );
            }
        }

        private static void AddStringHandlerOptIns(
            string masked,
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
                        !TypeScopeDerivesFromMessageAwareComponent(masked, typeStart)
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
                    if (TypeScopeDeclaresStringHandlerOptIn(masked, typeStart))
                    {
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

        private static bool TypeScopeDeclaresStringHandlerOptIn(string masked, int typeStart)
        {
            Regex memberRegex = new(
                @"\boverride\s+(?:(?:global::)?System\s*\.\s*)?(?:Boolean|bool)\s+RegisterForStringMessages\b",
                RegexOptions.CultureInvariant
            );
            foreach (Match match in memberRegex.Matches(masked, typeStart + 1))
            {
                if (FindContainingTypeStart(masked, match.Index) == typeStart)
                {
                    return true;
                }
                if (FindMatching(masked, typeStart, '{', '}') < match.Index)
                {
                    break;
                }
            }
            return false;
        }

        private static bool TypeScopeIsPartial(string masked, int typeStart)
        {
            Match declaration = null;
            foreach (Match match in TypeDeclarationRegex.Matches(masked))
            {
                if (IsOnPreprocessorDirectiveLine(masked, match.Index))
                {
                    continue;
                }
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

        private static bool IsOnPreprocessorDirectiveLine(string source, int index)
        {
            int lineStart = source.LastIndexOf('\n', index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            while (lineStart < index && (source[lineStart] == ' ' || source[lineStart] == '\t'))
            {
                lineStart++;
            }
            return lineStart < index && source[lineStart] == '#';
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

        private static void AddUniqueDeclarationReplacements(
            string masked,
            string methodName,
            HashSet<int> replacementStarts,
            SortedSet<string> manualReview,
            bool requireOverride,
            bool reportMissing = false,
            int usagePosition = -1
        )
        {
            List<ParameterList> declarations = FindMethodDeclarations(
                masked,
                methodName,
                requireOverride
            );
            if (0 <= usagePosition)
            {
                int usageScope = FindContainingTypeStart(masked, usagePosition);
                declarations = declarations.FindAll(declaration =>
                    FindContainingTypeStart(masked, declaration.DeclarationStart) == usageScope
                );
            }
            List<ParameterList> byRefDeclarations = declarations.FindAll(declaration =>
                RefRegex.IsMatch(
                    masked.Substring(
                        declaration.ParametersStart,
                        declaration.ParametersEnd - declaration.ParametersStart
                    )
                )
            );
            if (byRefDeclarations.Count == 0)
            {
                if (reportMissing && declarations.Count == 0)
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

            ParameterList declaration = byRefDeclarations[0];
            AddRefReplacements(
                masked,
                declaration.ParametersStart,
                declaration.ParametersEnd,
                replacementStarts
            );
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
            int typeScopeStart
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
                    @"(?:global::)?DxMessaging\s*\.\s*Unity\s*\.\s*MessageAwareComponent\b",
                    RegexOptions.CultureInvariant
                );
                if (qualifiedBase.Success && IsRootTypeQualifier(header, qualifiedBase.Index))
                {
                    return true;
                }
                Match unqualifiedBase = Regex.Match(
                    header,
                    @"\bMessageAwareComponent\b",
                    RegexOptions.CultureInvariant
                );
                return HasNamespaceImport(masked, "DxMessaging.Unity")
                    && !HasTypeOrAliasDeclaration(masked, "MessageAwareComponent")
                    && unqualifiedBase.Success
                    && IsRootTypeQualifier(header, unqualifiedBase.Index);
            }
            return false;
        }

        private static bool HasNamespaceImport(string masked, string namespaceName)
        {
            if (
                Regex.IsMatch(
                    masked,
                    @"^[ \t]*#(?:if|elif|else|endif)\b",
                    RegexOptions.CultureInvariant | RegexOptions.Multiline
                )
            )
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
                    + @"\b|\busing\s+"
                    + Regex.Escape(typeName)
                    + @"\s*=",
                RegexOptions.CultureInvariant
            );
        }

        private static bool IsRootTypeQualifier(string text, int typeStart)
        {
            int prefix = PreviousNonWhitespace(text, typeStart - 1);
            if (prefix < 0)
            {
                return true;
            }
            if (text[prefix] == '.')
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

        private static bool HasProvenTokenReceiver(string masked, int methodNameStart)
        {
            int dot = PreviousNonWhitespace(masked, methodNameStart - 1);
            if (dot < 0 || masked[dot] != '.')
            {
                return false;
            }
            int receiverEnd = PreviousNonWhitespace(masked, dot - 1);
            int receiverStart = receiverEnd;
            while (0 <= receiverStart && IsIdentifierPart(masked[receiverStart]))
            {
                receiverStart--;
            }
            receiverStart++;
            if (receiverEnd < receiverStart)
            {
                return false;
            }

            string receiver = masked.Substring(receiverStart, receiverEnd - receiverStart + 1);
            int usageScope = FindContainingTypeStart(masked, methodNameStart);
            if (receiver == "Token")
            {
                if (!TypeScopeDerivesFromMessageAwareComponent(masked, usageScope))
                {
                    return false;
                }
                Regex tokenShadow = new(
                    @"\b[A-Za-z_]\w*(?:\s*<[^;={}()]+>)?\s+Token\b",
                    RegexOptions.CultureInvariant
                );
                foreach (Match match in tokenShadow.Matches(masked))
                {
                    if (FindContainingTypeStart(masked, match.Index) == usageScope)
                    {
                        return false;
                    }
                }
                return true;
            }

            Regex declaration = new(
                @"(?<qualified>(?:global::)?DxMessaging\s*\.\s*Core\s*\.\s*)?\bMessageRegistrationToken\s+"
                    + Regex.Escape(receiver)
                    + @"\b",
                RegexOptions.CultureInvariant
            );
            Regex conflictingDeclaration = new(
                @"\b(?!MessageRegistrationToken\b)[A-Za-z_]\w*(?:\s*<[^;={}()]+>)?\s+"
                    + Regex.Escape(receiver)
                    + @"\b",
                RegexOptions.CultureInvariant
            );
            foreach (Match match in conflictingDeclaration.Matches(masked))
            {
                if (FindContainingTypeStart(masked, match.Index) == usageScope)
                {
                    return false;
                }
            }
            if (HasTypeOrAliasDeclaration(masked, "MessageRegistrationToken"))
            {
                return false;
            }
            foreach (Match match in declaration.Matches(masked))
            {
                if (FindContainingTypeStart(masked, match.Index) == usageScope)
                {
                    if (match.Groups["qualified"].Success)
                    {
                        return IsRootTypeQualifier(masked, match.Groups["qualified"].Index);
                    }
                    int typePrefix = PreviousNonWhitespace(masked, match.Index - 1);
                    return (
                            typePrefix < 0 || masked[typePrefix] != '.' && masked[typePrefix] != ':'
                        ) && HasNamespaceImport(masked, "DxMessaging.Core");
                }
            }
            return false;
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
                if (
                    TryGetMethodGroup(masked, callback, out string methodName, out _)
                    && FindMethodDeclarations(masked, methodName, requireOverride: false)
                        .Exists(declaration =>
                            RefRegex.IsMatch(
                                masked.Substring(
                                    declaration.ParametersStart,
                                    declaration.ParametersEnd - declaration.ParametersStart
                                )
                            )
                        )
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
            bool requireProvenReceiver
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
                    if (requireProvenReceiver && !HasProvenTokenReceiver(masked, match.Index))
                    {
                        if (manualReview != null && InvocationMayNeedMigration(masked, invocation))
                        {
                            manualReview.Add(
                                $"{name} has a receiver whose MessageRegistrationToken type cannot be proven"
                            );
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
            HashSet<string> groups = new(StringComparer.Ordinal);
            foreach (Invocation invocation in invocations)
            {
                foreach (TextSpan callback in GetCallbackArguments(masked, invocation))
                {
                    if (TryGetMethodGroup(masked, callback, out string methodName, out _))
                    {
                        groups.Add(ScopedMethodKey(masked, methodName, callback.Start));
                    }
                }
            }
            return groups;
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
                @"^\s*(?<name>[A-Za-z_]\w*)\s*:\s*",
                RegexOptions.CultureInvariant
            );
            if (!match.Success)
            {
                argumentName = string.Empty;
                value = argument;
                return false;
            }

            argumentName = match.Groups["name"].Value;
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
                        AddRefReplacements(
                            masked,
                            parameterStart + 1,
                            parameterEnd,
                            replacementStarts
                        );
                    }
                    return;
                }
            }

            int start = NextNonWhitespace(masked, invocation.Start);
            if (!StartsWithIdentifier(masked, start, "delegate"))
            {
                return;
            }
            int open = NextNonWhitespace(masked, start + "delegate".Length);
            if (open < 0 || invocation.End <= open || masked[open] != '(')
            {
                return;
            }
            int close = FindMatching(masked, open, '(', ')');
            if (0 <= close && close < invocation.End)
            {
                AddRefReplacements(masked, open + 1, close, replacementStarts);
            }
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
            Match match = RefRegex.Match(text, start);
            while (match.Success && match.Index < end)
            {
                replacementStarts.Add(match.Index);
                match = match.NextMatch();
            }
        }

        private static void AddBaseOverrideForwardingReplacements(
            string masked,
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

            List<TextSpan> overrideParameters = SplitArguments(
                masked,
                declaration.ParametersStart,
                declaration.ParametersEnd
            );
            List<(string Parameter, TextSpan Declaration)> localShadows =
                CollectLocalFunctionShadows(masked, body);
            Regex baseCall = new(
                @"\bbase\s*\.\s*" + Regex.Escape(methodName) + @"\s*\(",
                RegexOptions.CultureInvariant
            );
            foreach (Match match in baseCall.Matches(masked, body.Start))
            {
                if (body.Start + body.Length <= match.Index)
                {
                    break;
                }
                int open = masked.IndexOf('(', match.Index, match.Length);
                int close = FindMatching(masked, open, '(', ')');
                if (close < 0 || body.Start + body.Length < close)
                {
                    continue;
                }
                List<TextSpan> arguments = SplitArguments(masked, open + 1, close);
                for (int argumentIndex = 0; argumentIndex < arguments.Count; argumentIndex++)
                {
                    TextSpan argument = GetArgumentValueSpan(masked, arguments[argumentIndex]);
                    int refStart = NextNonWhitespace(masked, argument.Start);
                    if (
                        argument.Start + argument.Length <= refStart
                        || !StartsWithIdentifier(masked, refStart, "ref")
                    )
                    {
                        continue;
                    }

                    // A withheld base-argument edit also defers every callback that contains it.
                    baseForwardingOwners[refStart] = declaration.ParametersStart - 1;
                    Match forwarded = BaseRefArgumentRegex.Match(
                        masked.Substring(argument.Start, argument.Length)
                    );
                    if (
                        forwarded.Success
                        && argument.Start == arguments[argumentIndex].Start
                        && argumentIndex < overrideParameters.Count
                        && CanRewriteBaseArgument(
                            masked,
                            match.Index,
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
                && SplitArguments(masked, local.Start + 1, close)
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
            List<TextSpan> interpolatedExpressions,
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
                    removedBaseForwarding |= PruneCallback(
                        source,
                        masked,
                        interpolatedExpressions,
                        replacementStarts,
                        baseForwardingOwners,
                        manualReview,
                        entry
                    );
                }
            } while (removedBaseForwarding);
        }

        private static bool PruneCallback(
            string source,
            string masked,
            List<TextSpan> interpolatedExpressions,
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
                CollectLocalFunctionShadows(masked, body);

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
                    interpolatedExpressions,
                    enclosingOverride,
                    entry.Key
                )
            )
            {
                reason = "has a direct local function call that depends on its 'ref' parameter";
            }
            reason ??= FindParameterUseIssue(
                source,
                masked,
                body,
                interpolatedExpressions,
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
                $"callback on line {LineNumber(masked, entry.Key)} {reason}; inspect it manually"
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
            foreach (TextSpan expression in interpolatedExpressions)
            {
                if (
                    scope.Start <= expression.Start
                    && expression.Start + expression.Length <= scopeEnd
                )
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
            TextSpan body
        )
        {
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
            // Conditional compilation can change the declaration or the end of its scope.
            if (0 <= masked.IndexOf('#', body.Start, body.Length))
            {
                yield break;
            }

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
                if (body.Start <= before && "{;}:".IndexOf(masked[before]) < 0)
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

                foreach (TextSpan parameter in SplitArguments(masked, open + 1, close))
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
            ExpandStandaloneGrouping(source, rightHandSide, ref targetStart, ref following);
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
            foreach (TextSpan scope in interpolatedExpressions)
            {
                if (bodyEnd <= scope.Start)
                {
                    break;
                }
                if (scope.Start < body.Start || bodyEnd < scope.Start + scope.Length)
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
                            || IsDirectFixedInitializer(masked, body, targetStart, next)
                        )
                    )
                )
                {
                    return $"invokes a method on parameter '{parameterName}' or its member";
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
                if (text[after] is not ('.' or '[' or ')' or '?' or ',') && !postfixWrite)
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
                    && !FollowsTargetBoundary(text, body.Start, beforeOpening)
                    && "+-*/%&|^~<".IndexOf(text[beforeOpening]) < 0
                    && !FollowsRangeOperator(text, body.Start, beforeOpening)
                    && !FollowsStandaloneGreaterThan(text, body.Start, beforeOpening)
                    && !FollowsGroupingOperandKeyword(text, body.Start, beforeOpening)
                )
                {
                    return;
                }

                targetStart = groupingStart;
                following = NextAfterComments(text, following + 1, bodyEnd);
            }
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
                    && 1 < SplitArguments(text, opening + 1, closing).Count;
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

                List<TextSpan> elements = SplitArguments(
                    text,
                    tupleStart + 1,
                    tupleEnd,
                    ignoreNestedAngles: true
                );
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
            foreach (TextSpan parameter in SplitArguments(text, start, end))
            {
                string parameterText = text.Substring(parameter.Start, parameter.Length);
                if (!RefRegex.IsMatch(parameterText))
                {
                    continue;
                }
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
            foreach (TextSpan parameter in SplitArguments(text, parametersStart, parametersEnd))
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
                    case '<':
                        if (IsGenericOpenAngle(text, index))
                        {
                            angles++;
                        }
                        break;
                    case '>':
                        if (0 < angles)
                        {
                            angles--;
                        }
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

        private static bool IsGenericOpenAngle(string text, int openAngle)
        {
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            int angles = 1;
            for (int index = openAngle + 1; index < text.Length; index++)
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
                    case '<':
                        angles++;
                        break;
                    case '>':
                        angles--;
                        if (angles == 0)
                        {
                            int next = NextNonWhitespace(text, index + 1);
                            return next < 0 || IsGenericAngleFollower(text[next]);
                        }
                        break;
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

        private static int LineNumber(string text, int position)
        {
            int line = 1;
            for (int index = 0; index < position; index++)
            {
                char current = text[index];
                if (
                    CSharpLineTerminators.Contains(current)
                    && (current != '\n' || index == 0 || text[index - 1] != '\r')
                )
                {
                    line++;
                }
            }
            return line;
        }

        private static List<TextSpan> SplitArguments(
            string text,
            int start,
            int end,
            bool ignoreNestedAngles = false
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
                    // Nested expressions already protect their commas, including in comparisons.
                    case '<'
                    or '>'
                        when !ignoreNestedAngles
                            || (parentheses == 0 && brackets == 0 && braces == 0):
                        angles = text[index] == '<' ? angles + 1 : Math.Max(0, angles - 1);
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

        private static string MaskNonCode(string source, List<TextSpan> interpolatedExpressions)
        {
            char[] masked = source.ToCharArray();
            for (int index = 0; index < source.Length; )
            {
                int end = FindNonCodeEnd(source, index, interpolatedExpressions);
                if (index < end)
                {
                    Mask(masked, index, end);
                    index = end;
                }
                else
                {
                    index++;
                }
            }
            return new string(masked);
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

        private static bool IsMessageHandlerQualified(string text, int fastHandlerStart)
        {
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

            int handlerQualifier = PreviousNonWhitespace(text, identifierStart - 1);
            return HasDxMessagingCoreQualifier(text, identifierStart)
                || (
                    handlerQualifier < 0
                    || text[handlerQualifier] != '.' && text[handlerQualifier] != ':'
                ) && HasNamespaceImport(text, "DxMessaging.Core");
        }

        private static bool HasDxMessagingCoreQualifier(string text, int messageHandlerStart)
        {
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
            bool rootQualified =
                qualifierPrefix < 0
                || text[qualifierPrefix] != '.' && text[qualifierPrefix] != ':'
                || Regex.IsMatch(
                    text.Substring(0, dxMessagingStart),
                    @"\bglobal\s*::\s*$",
                    RegexOptions.CultureInvariant
                );
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
