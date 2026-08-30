namespace DxMessaging.Editor
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;
    using UnityEditor;
    using UnityEngine;

    /// <summary>
    /// Upgrades 3.x fast-handler callback parameters from <c>ref</c> to <c>in</c>.
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

        private static readonly Regex IdentifierRegexForParameter = new(
            @"@?[A-Za-z_]\w*",
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

                if (result.ReplacementCount > 0)
                {
                    upgrades.Add(
                        new FileUpgrade(
                            fullPath,
                            originalBytes,
                            upgradedBytes,
                            result.ReplacementCount
                        )
                    );
                }
            }

            int replacementCount = 0;
            foreach (FileUpgrade upgrade in upgrades)
            {
                replacementCount += upgrade.ReplacementCount;
            }

            if (replacementCount == 0)
            {
                string suffix =
                    manualReview.Count == 0
                        ? string.Empty
                        : $"\n\n{manualReview.Count} item(s) require manual review. "
                            + "See the Console for details.";
                EditorUtility.DisplayDialog(
                    "DxMessaging Fast Handler Upgrade",
                    "No 3.x fast-handler parameters were found under Assets." + suffix,
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
                $"Update {replacementCount} parameter(s) in {upgrades.Count} script(s)?\n\n"
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
                    $"[DxMessaging] Upgraded {replacementCount} fast-handler parameter(s) in "
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

            string masked = MaskNonCode(source);
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

            foreach (string overrideName in ChangedOverrideNames)
            {
                AddOverrideDeclarationReplacements(
                    masked,
                    overrideName,
                    replacementStarts,
                    manualReview
                );
            }

            PruneUnsafeByReferenceUse(masked, replacementStarts, manualReview);

            List<int> orderedStarts = new(replacementStarts);
            orderedStarts.Sort();
            StringBuilder upgraded = new(source.Length);
            int previous = 0;
            foreach (int start in orderedStarts)
            {
                upgraded.Append(source, previous, start - previous);
                upgraded.Append("in");
                previous = start + 3;
            }
            upgraded.Append(source, previous, source.Length - previous);

            return new UpgradeResult(
                upgraded.ToString(),
                orderedStarts.Count,
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
                if (position + 1 >= header.Length)
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
                || normalized.IndexOf("/Generated/", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("/GeneratedCode/", StringComparison.OrdinalIgnoreCase) >= 0;
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
            if (genericClose < 0 || genericClose >= equals)
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
                    replacementStarts
                );
            }
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
            if (usagePosition >= 0)
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
                if (previous >= 0 && masked[previous] == '.')
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
            if (prefix.IndexOf('=') >= 0 || prefix.IndexOf("=>", StringComparison.Ordinal) >= 0)
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
            return body >= 0 && (expressionBody < 0 || body < expressionBody)
                || expressionBody >= 0;
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
                if (match.Index >= position)
                {
                    break;
                }

                int open = masked.IndexOf('{', match.Index + match.Length);
                if (open < 0 || open >= position)
                {
                    continue;
                }
                int semicolon = masked.IndexOf(';', match.Index + match.Length);
                if (semicolon >= 0 && semicolon < open)
                {
                    continue;
                }
                int close = FindMatching(masked, open, '{', '}');
                if (close >= position && open > containingStart)
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
            while (receiverStart >= 0 && IsIdentifierPart(masked[receiverStart]))
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
                if (cursor >= 0 && masked[cursor] == '<')
                {
                    cursor = FindMatching(masked, cursor, '<', '>');
                    cursor = cursor < 0 ? -1 : NextNonWhitespace(masked, cursor + 1);
                }
                if (cursor < 0 || masked[cursor] != '(')
                {
                    continue;
                }

                int close = FindMatching(masked, cursor, '(', ')');
                if (close >= 0)
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
            if (callbackIndex >= 0 && callbackIndex < arguments.Count)
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
            if (!match.Success && value.Length >= 2 && value[0] == '(' && value[^1] == ')')
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
            if (arrow >= 0)
            {
                int parameterEnd = PreviousNonWhitespace(masked, arrow - 1);
                if (parameterEnd >= invocation.Start && masked[parameterEnd] == ')')
                {
                    int parameterStart = FindMatchingBackward(masked, parameterEnd, '(', ')');
                    if (
                        parameterStart >= invocation.Start
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
            if (open < 0 || open >= invocation.End || masked[open] != '(')
            {
                return;
            }
            int close = FindMatching(masked, open, '(', ')');
            if (close >= 0 && close < invocation.End)
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
            HashSet<int> replacementStarts
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

            Regex baseCall = new(
                @"\bbase\s*\.\s*" + Regex.Escape(methodName) + @"\s*\(",
                RegexOptions.CultureInvariant
            );
            foreach (Match match in baseCall.Matches(masked, body.Start))
            {
                if (match.Index >= body.Start + body.Length)
                {
                    break;
                }
                int open = masked.IndexOf('(', match.Index, match.Length);
                int close = FindMatching(masked, open, '(', ')');
                if (close < 0 || close > body.Start + body.Length)
                {
                    continue;
                }
                foreach (TextSpan argument in SplitArguments(masked, open + 1, close))
                {
                    string argumentText = masked.Substring(argument.Start, argument.Length);
                    Match forwarded = Regex.Match(
                        argumentText,
                        @"^\s*ref\s+(?<name>@?[A-Za-z_]\w*)\s*$",
                        RegexOptions.CultureInvariant
                    );
                    if (
                        forwarded.Success
                        && refParameterNames.Contains(forwarded.Groups["name"].Value)
                    )
                    {
                        int refStart = argument.Start + forwarded.Index;
                        refStart = NextNonWhitespace(masked, refStart);
                        replacementStarts.Add(refStart);
                    }
                }
            }
        }

        private static void PruneUnsafeByReferenceUse(
            string masked,
            HashSet<int> replacementStarts,
            SortedSet<string> manualReview
        )
        {
            Dictionary<int, List<int>> parameterReplacementsByOpen = new();
            foreach (int replacementStart in replacementStarts)
            {
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

            foreach (KeyValuePair<int, List<int>> entry in parameterReplacementsByOpen)
            {
                int close = FindMatching(masked, entry.Key, '(', ')');
                if (close < 0 || !TryFindCallableBody(masked, close, out TextSpan body))
                {
                    continue;
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
                    if (parameterName.Length > 0)
                    {
                        parameterNames.Add(parameterName);
                    }
                    else
                    {
                        hasUnsupportedParameter = true;
                    }
                }

                List<int> forwardingStarts = new();
                string unsafeParameter = string.Empty;
                string unsafeModifier = string.Empty;
                bool hasUnsafeByReferenceUse = false;
                foreach (string parameterName in parameterNames)
                {
                    Regex byReferenceUse = new(
                        @"\b(?<modifier>ref|out)\s+" + Regex.Escape(parameterName) + @"\b",
                        RegexOptions.CultureInvariant
                    );
                    foreach (Match match in byReferenceUse.Matches(masked, body.Start))
                    {
                        if (match.Index >= body.Start + body.Length)
                        {
                            break;
                        }
                        forwardingStarts.Add(match.Index);
                        if (!replacementStarts.Contains(match.Index))
                        {
                            hasUnsafeByReferenceUse = true;
                            unsafeParameter = parameterName;
                            unsafeModifier = match.Groups["modifier"].Value;
                        }
                    }
                }

                if (!hasUnsafeByReferenceUse && !hasUnsupportedParameter)
                {
                    continue;
                }
                foreach (int replacementStart in entry.Value)
                {
                    replacementStarts.Remove(replacementStart);
                }
                foreach (int forwardingStart in forwardingStarts)
                {
                    replacementStarts.Remove(forwardingStart);
                }
                string reason =
                    hasUnsupportedParameter && !hasUnsafeByReferenceUse
                        ? "has unsupported parameter syntax"
                        : $"passes parameter '{unsafeParameter}' by {unsafeModifier}";
                manualReview.Add(
                    $"callback on line {LineNumber(masked, entry.Key)} {reason}; inspect it manually"
                );
            }
        }

        private static int FindEnclosingOpenParenthesis(string text, int position)
        {
            int depth = 0;
            for (int index = position - 1; index >= 0; index--)
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
                MatchCollection identifiers = IdentifierRegexForParameter.Matches(parameterText);
                if (identifiers.Count > 0)
                {
                    names.Add(identifiers[identifiers.Count - 1].Value);
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
                    || modifierStart >= parameter.Start + parameter.Length
                )
                {
                    continue;
                }
                MatchCollection identifiers = IdentifierRegexForParameter.Matches(
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
                if (semicolon >= 0)
                {
                    if (blockStart > semicolon)
                    {
                        blockStart = -1;
                    }
                    if (expressionStart > semicolon)
                    {
                        expressionStart = -1;
                    }
                }
            }
            if (blockStart >= 0 && (expressionStart < 0 || blockStart < expressionStart))
            {
                int blockEnd = FindMatching(text, blockStart, '{', '}');
                if (blockEnd >= 0)
                {
                    body = new TextSpan(blockStart + 1, blockEnd - blockStart - 1);
                    return true;
                }
            }
            if (expressionStart >= 0)
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
                        if (angles > 0)
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
                if (text[index] == '\n')
                {
                    line++;
                }
            }
            return line;
        }

        private static List<TextSpan> SplitArguments(string text, int start, int end)
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
                    case '<':
                        angles++;
                        break;
                    case '>':
                        angles = Math.Max(0, angles - 1);
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

        private static string MaskNonCode(string source)
        {
            char[] masked = source.ToCharArray();
            int index = 0;
            while (index < source.Length)
            {
                if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == '/')
                {
                    int end = source.IndexOf('\n', index + 2);
                    end = end < 0 ? source.Length : end;
                    Mask(masked, index, end);
                    index = end;
                    continue;
                }
                if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == '*')
                {
                    int end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    end = end < 0 ? source.Length : end + 2;
                    Mask(masked, index, end);
                    index = end;
                    continue;
                }

                int quoteStart = index;
                if (source[index] == '$' && index + 1 < source.Length && source[index + 1] == '@')
                {
                    quoteStart = index + 2;
                }
                else if (
                    source[index] == '@'
                    && index + 1 < source.Length
                    && source[index + 1] == '$'
                )
                {
                    quoteStart = index + 2;
                }
                else if (
                    (source[index] == '$' || source[index] == '@')
                    && index + 1 < source.Length
                )
                {
                    quoteStart = index + 1;
                }

                if (quoteStart < source.Length && source[quoteStart] == '"')
                {
                    bool verbatim =
                        source[index] == '@'
                        || (index + 1 < source.Length && source[index + 1] == '@');
                    int quoteCount = CountRun(source, quoteStart, '"');
                    int end =
                        quoteCount >= 3
                            ? FindRawStringEnd(source, quoteStart + quoteCount, quoteCount)
                            : FindQuotedEnd(source, quoteStart + 1, '"', verbatim);
                    Mask(masked, index, end);
                    index = end;
                    continue;
                }
                if (source[index] == '\'')
                {
                    int end = FindQuotedEnd(source, index + 1, '\'', verbatim: false);
                    Mask(masked, index, end);
                    index = end;
                    continue;
                }

                index++;
            }
            return new string(masked);
        }

        private static int FindQuotedEnd(string source, int start, char quote, bool verbatim)
        {
            for (int index = start; index < source.Length; index++)
            {
                if (source[index] != quote)
                {
                    if (!verbatim && source[index] == '\\')
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

        private static int FindRawStringEnd(string source, int start, int quoteCount)
        {
            string terminator = new('"', quoteCount);
            int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
            return end < 0 ? source.Length : end + quoteCount;
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
                if (text[index] != '\r' && text[index] != '\n')
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
            char closeValue
        )
        {
            int depth = 0;
            for (int index = close; index >= 0; index--)
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
            for (int index = start; index >= 0; index--)
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
                || start + identifier.Length > text.Length
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
            while (identifierStart >= 0 && IsIdentifierPart(text[identifierStart]))
            {
                identifierStart--;
            }
            identifierStart++;
            bool correctIdentifier =
                identifierEnd >= identifierStart
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
            while (coreStart >= 0 && IsIdentifierPart(text[coreStart]))
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
            while (dxMessagingStart >= 0 && IsIdentifierPart(text[dxMessagingStart]))
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
                && dxMessagingEnd >= dxMessagingStart
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
                for (int index = applied.Count - 1; index >= 0; index--)
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
                    Debug.LogWarning(
                        $"[DxMessaging] Updated {file.FullPath}, but could not remove backup "
                            + $"{file.BackupPath}: {cleanupFailure.Message}"
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
                IReadOnlyList<string> manualReviewMethods
            )
            {
                UpgradedSource = upgradedSource;
                ReplacementCount = replacementCount;
                ManualReviewMethods = manualReviewMethods;
            }

            public string UpgradedSource { get; }

            public int ReplacementCount { get; }

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
                int replacementCount
            )
            {
                FullPath = fullPath;
                OriginalBytes = originalBytes;
                UpgradedBytes = upgradedBytes;
                ReplacementCount = replacementCount;
            }

            public string FullPath { get; }

            public byte[] OriginalBytes { get; }

            public byte[] UpgradedBytes { get; }

            public int ReplacementCount { get; }
        }

        internal readonly struct PendingFileUpgrade
        {
            public PendingFileUpgrade(string fullPath, byte[] originalBytes, byte[] upgradedBytes)
            {
                FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
                OriginalBytes =
                    originalBytes ?? throw new ArgumentNullException(nameof(originalBytes));
                UpgradedBytes =
                    upgradedBytes ?? throw new ArgumentNullException(nameof(upgradedBytes));
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
                if (preambleLength == 0 && Array.IndexOf(bytes, (byte)0) >= 0)
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
