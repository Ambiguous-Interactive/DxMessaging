#if UNITY_EDITOR
namespace DxMessaging.Editor.Windows
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Core;
    using DxMessaging.Editor;
    using UnityEditor;
    using UnityEditor.Compilation;
    using UnityEngine;
    using UnityEngine.UIElements;

    /// <summary>
    /// Shared editor plumbing that turns captured diagnostic text back into something a
    /// reader can act on: the C# declaration a message type was written in, the source line
    /// a stack frame points at, and the button that opens either one. Both diagnostics
    /// windows draw on it, so a fix to how a link resolves reaches the Flow Graph and the
    /// Message Monitor at once.
    /// </summary>
    internal static class DxMessagingEditorSourceLinks
    {
        /// <summary>
        /// Name shared by every "open this in your editor" button both diagnostics windows
        /// build, so a test can find one the same way wherever it was rendered.
        /// </summary>
        internal const string SourceLinkButtonName = "dxmessaging-source-link";

        internal readonly struct SourceLocation
        {
            internal SourceLocation(string assetPath, int line)
            {
                AssetPath = assetPath ?? string.Empty;
                Line = Math.Max(1, line);
            }

            internal string AssetPath { get; }

            internal int Line { get; }

            internal bool IsValid => !string.IsNullOrWhiteSpace(AssetPath);
        }

        private enum SourceScopeKind
        {
            Namespace,
            Type,
        }

        private readonly struct SourceScope
        {
            internal SourceScope(SourceScopeKind kind, string name, int bodyDepth)
            {
                Kind = kind;
                Name = name ?? string.Empty;
                BodyDepth = bodyDepth;
            }

            internal SourceScopeKind Kind { get; }

            internal string Name { get; }

            internal int BodyDepth { get; }
        }

        private readonly struct SourceTypeDeclaration
        {
            internal SourceTypeDeclaration(
                string sourceNamespace,
                IReadOnlyList<string> typeNames,
                int line
            )
            {
                Namespace = sourceNamespace ?? string.Empty;
                TypeNames = typeNames?.ToArray() ?? Array.Empty<string>();
                Line = line;
            }

            internal string Namespace { get; }

            internal IReadOnlyList<string> TypeNames { get; }

            internal int Line { get; }
        }

        private readonly struct MessageSourceFile
        {
            internal MessageSourceFile(string fullPath, string assetPath)
            {
                FullPath = fullPath ?? string.Empty;
                AssetPath = assetPath ?? string.Empty;
            }

            internal string FullPath { get; }

            internal string AssetPath { get; }
        }

        internal readonly struct CapturedTypeIdentity
        {
            internal CapturedTypeIdentity(string typeName, string assemblyName)
            {
                TypeName = typeName ?? string.Empty;
                AssemblyName = assemblyName ?? string.Empty;
            }

            internal string TypeName { get; }

            internal string AssemblyName { get; }

            internal string CacheKey =>
                string.IsNullOrWhiteSpace(AssemblyName) ? TypeName : $"{TypeName} [{AssemblyName}]";
        }

        private sealed class MessageSourceAssemblyIndex
        {
            internal MessageSourceAssemblyIndex(Task<MessageSourceIndexBuildResult> buildTask)
            {
                BuildTask = buildTask ?? throw new ArgumentNullException(nameof(buildTask));
            }

            internal Task<MessageSourceIndexBuildResult> BuildTask { get; set; }

            internal IReadOnlyDictionary<string, SourceLocation> Locations { get; set; }

            internal bool IsComplete { get; set; }

            internal DateTime RetryAfterUtc { get; set; }
        }

        private readonly struct MessageSourceIndexBuildResult
        {
            internal MessageSourceIndexBuildResult(
                IReadOnlyDictionary<string, SourceLocation> locations,
                bool isComplete
            )
            {
                Locations = locations;
                IsComplete = isComplete;
            }

            internal IReadOnlyDictionary<string, SourceLocation> Locations { get; }

            internal bool IsComplete { get; }
        }

        private static readonly Dictionary<string, SourceLocation> MessageSourceLocationCache = new(
            StringComparer.Ordinal
        );
        private static readonly Dictionary<
            string,
            MessageSourceAssemblyIndex
        > MessageSourceAssemblyIndexes = new(StringComparer.Ordinal);
        private static Func<string, string[]> _messageSourceFileReader = File.ReadAllLines;
        internal static event Action MessageSourceIndexChanged;
        private static readonly Regex SourceNamespaceDeclarationPattern = new(
            @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*([;{]?)",
            RegexOptions.CultureInvariant
        );
        private static readonly Regex SourceTypeDeclarationPattern = new(
            @"^\s*(?:\[[^\]\r\n]+\]\s*)*(?:(?:public|internal|protected|private|abstract|sealed|static|partial|readonly|ref|unsafe|new|file)\s+)*(?:(?:record(?:\s+(?:class|struct))?)|class|struct|interface|enum)\s+(@?[A-Za-z_][A-Za-z0-9_]*)\b",
            RegexOptions.CultureInvariant
        );

        /// <summary>
        /// Text that only ever appears in the frames Unity's own stack capture leaves on top
        /// of every trace it hands back. They name the capture, never the code that emitted,
        /// so no reader wants to see them and no line number in them is worth opening.
        /// </summary>
        private static readonly string[] StackCaptureFrameMarkers =
        {
            "UnityEngine.Debug:ExtractStackTrace",
            "UnityEngine.StackTraceUtility",
            "StackTraceUtility:ExtractStackTrace",
        };

        private static readonly string[] StackTraceLineSeparators = { "\r\n", "\n", "\r" };

        internal static bool IsStackCaptureFrame(string frame)
        {
            if (string.IsNullOrWhiteSpace(frame))
            {
                return false;
            }

            foreach (string marker in StackCaptureFrameMarkers)
            {
                if (frame.IndexOf(marker, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Splits a captured stack trace into the frames that describe the emitting code,
        /// dropping the blank lines and the capture frames Unity adds on top. This is the one
        /// definition of "a frame worth showing"; every surface that renders a trace uses it.
        /// </summary>
        internal static IReadOnlyList<string> ReadCallSiteFrames(string stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
            {
                return Array.Empty<string>();
            }

            return stackTrace
                .Split(StackTraceLineSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line) && !IsStackCaptureFrame(line))
                .ToArray();
        }

        internal static string CreateEmissionSite(string stackTrace)
        {
            IReadOnlyList<string> frames = ReadCallSiteFrames(stackTrace);
            return frames.Count == 0 ? "<unknown call site>" : frames[0];
        }

        internal static string CreateCompactCallSiteLabel(string value)
        {
            string callSite = value ?? string.Empty;
            int sourceStart = callSite.LastIndexOf(" (at ", StringComparison.Ordinal);
            if (sourceStart >= 0)
            {
                callSite = callSite.Substring(0, sourceStart);
            }
            callSite = callSite.Trim().Replace(" ()", "()");
            int methodSeparator = Math.Max(callSite.LastIndexOf(':'), callSite.LastIndexOf('.'));
            if (methodSeparator <= 0 || methodSeparator >= callSite.Length - 1)
            {
                return string.IsNullOrWhiteSpace(callSite) ? "unknown call site" : callSite;
            }

            string owner = callSite.Substring(0, methodSeparator);
            string method = callSite.Substring(methodSeparator + 1);
            int ownerSeparator = Math.Max(owner.LastIndexOf('.'), owner.LastIndexOf('+'));
            if (ownerSeparator >= 0 && ownerSeparator < owner.Length - 1)
            {
                owner = owner.Substring(ownerSeparator + 1);
            }
            return owner + "." + method;
        }

        internal static bool TryParseSourceLocation(string text, out SourceLocation location)
        {
            location = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            int pathStart = text.LastIndexOf("(at ", StringComparison.Ordinal);
            int pathEnd = text.LastIndexOf(')');
            if (pathStart < 0 || pathEnd <= pathStart + 4)
            {
                return false;
            }

            int lineSeparator = text.LastIndexOf(':', pathEnd - 1);
            if (
                lineSeparator <= pathStart + 4
                || !int.TryParse(
                    text.Substring(lineSeparator + 1, pathEnd - lineSeparator - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int line
                )
                || line <= 0
            )
            {
                return false;
            }

            string assetPath = text.Substring(pathStart + 4, lineSeparator - pathStart - 4)
                .Replace('\\', '/');
            if (Path.IsPathRooted(assetPath))
            {
                assetPath = (FileUtil.GetProjectRelativePath(assetPath) ?? string.Empty).Replace(
                    '\\',
                    '/'
                );
            }
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            location = new SourceLocation(assetPath, line);
            return true;
        }

        internal static bool TryResolveMessageSource(
            string messageTypeName,
            out SourceLocation location
        )
        {
            location = default;
            CapturedTypeIdentity identity = ParseCapturedTypeIdentity(messageTypeName);
            if (string.IsNullOrWhiteSpace(identity.TypeName))
            {
                return false;
            }

            if (MessageSourceLocationCache.TryGetValue(identity.CacheKey, out location))
            {
                return location.IsValid;
            }

            System.Reflection.Assembly[] loadedAssemblies =
                System.AppDomain.CurrentDomain.GetAssemblies();
            IEnumerable<System.Reflection.Assembly> candidateAssemblies = loadedAssemblies;
            if (!string.IsNullOrWhiteSpace(identity.AssemblyName))
            {
                candidateAssemblies = candidateAssemblies.Where(assembly =>
                    string.Equals(
                        assembly.GetName().Name,
                        identity.AssemblyName,
                        StringComparison.Ordinal
                    )
                );
            }

            Type messageType = candidateAssemblies
                .Select(assembly => assembly.GetType(identity.TypeName, throwOnError: false))
                .FirstOrDefault(candidate => candidate != null);
            messageType ??= TypeCache
                .GetTypesDerivedFrom<IMessage>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.FullName, identity.TypeName, StringComparison.Ordinal)
                    && IsTypeFromCapturedAssembly(candidate, identity.AssemblyName)
                );
            if (
                messageType == null
                && identity.TypeName.IndexOf('.') < 0
                && identity.TypeName.IndexOf('+') < 0
            )
            {
                Type[] simpleNameMatches = TypeCache
                    .GetTypesDerivedFrom<IMessage>()
                    .Where(candidate =>
                        string.Equals(candidate.Name, identity.TypeName, StringComparison.Ordinal)
                        && IsTypeFromCapturedAssembly(candidate, identity.AssemblyName)
                    )
                    .Take(2)
                    .ToArray();
                messageType = simpleNameMatches.Length == 1 ? simpleNameMatches[0] : null;
            }
            if (messageType == null)
            {
                MessageSourceLocationCache[identity.CacheKey] = default;
                return false;
            }

            string compilationAssemblyName = string.IsNullOrWhiteSpace(identity.AssemblyName)
                ? messageType.Assembly.GetName().Name
                : identity.AssemblyName;
            UnityEditor.Compilation.Assembly compilationAssembly = CompilationPipeline
                .GetAssemblies()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.name, compilationAssemblyName, StringComparison.Ordinal)
                );
            if (compilationAssembly == null)
            {
                MessageSourceLocationCache[identity.CacheKey] = default;
                return false;
            }

            string declarationKey = CreateSourceDeclarationKey(
                messageType.Namespace,
                CreateSourceTypeNameChain(messageType)
            );
            if (
                MessageSourceAssemblyIndexes.TryGetValue(
                    compilationAssembly.name,
                    out MessageSourceAssemblyIndex existingIndex
                )
            )
            {
                if (existingIndex.Locations == null)
                {
                    return false;
                }

                if (existingIndex.Locations.TryGetValue(declarationKey, out location))
                {
                    MessageSourceLocationCache[identity.CacheKey] = location;
                    return true;
                }

                if (!existingIndex.IsComplete)
                {
                    if (
                        existingIndex.BuildTask == null
                        && DateTime.UtcNow >= existingIndex.RetryAfterUtc
                    )
                    {
                        existingIndex.BuildTask = CreateMessageSourceIndexBuildTask(
                            compilationAssembly
                        );
                        RegisterMessageSourceIndexDrain();
                    }
                    return false;
                }

                MessageSourceLocationCache[identity.CacheKey] = default;
                return false;
            }

            MessageSourceAssemblyIndexes.Add(
                compilationAssembly.name,
                new MessageSourceAssemblyIndex(
                    CreateMessageSourceIndexBuildTask(compilationAssembly)
                )
            );
            RegisterMessageSourceIndexDrain();
            return false;
        }

        private static bool IsTypeFromCapturedAssembly(Type type, string assemblyName)
        {
            return string.IsNullOrWhiteSpace(assemblyName)
                || string.Equals(
                    type.Assembly.GetName().Name,
                    assemblyName,
                    StringComparison.Ordinal
                );
        }

        private static Task<MessageSourceIndexBuildResult> CreateMessageSourceIndexBuildTask(
            UnityEditor.Compilation.Assembly compilationAssembly
        )
        {
            MessageSourceFile[] sourceFiles = compilationAssembly
                .sourceFiles.Select(CreateMessageSourceFile)
                .Where(sourceFile =>
                    !string.IsNullOrWhiteSpace(sourceFile.FullPath)
                    && !string.IsNullOrWhiteSpace(sourceFile.AssetPath)
                )
                .ToArray();
            Func<string, string[]> fileReader = _messageSourceFileReader;
            return Task.Run(() => BuildMessageSourceAssemblyIndex(sourceFiles, fileReader));
        }

        private static void RegisterMessageSourceIndexDrain()
        {
            EditorApplication.update -= DrainMessageSourceAssemblyIndexes;
            EditorApplication.update += DrainMessageSourceAssemblyIndexes;
        }

        private static MessageSourceFile CreateMessageSourceFile(string sourceFile)
        {
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                return default;
            }

            string fullPath = Path.IsPathRooted(sourceFile)
                ? sourceFile
                : Path.GetFullPath(sourceFile);
            string assetPath = Path.IsPathRooted(sourceFile)
                ? FileUtil.GetProjectRelativePath(fullPath)
                : sourceFile;
            assetPath = (assetPath ?? string.Empty).Replace('\\', '/');
            return string.IsNullOrWhiteSpace(assetPath)
                ? default
                : new MessageSourceFile(fullPath, assetPath);
        }

        private static MessageSourceIndexBuildResult BuildMessageSourceAssemblyIndex(
            IReadOnlyList<MessageSourceFile> sourceFiles,
            Func<string, string[]> readAllLines
        )
        {
            Dictionary<string, SourceLocation> locations = new(StringComparer.Ordinal);
            if (sourceFiles == null || readAllLines == null)
            {
                return new MessageSourceIndexBuildResult(locations, isComplete: false);
            }

            bool isComplete = true;
            foreach (MessageSourceFile sourceFile in sourceFiles)
            {
                string[] lines;
                try
                {
                    lines = readAllLines(sourceFile.FullPath);
                }
                catch (IOException)
                {
                    isComplete = false;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    isComplete = false;
                    continue;
                }

                foreach (SourceTypeDeclaration declaration in FindSourceTypeDeclarations(lines))
                {
                    string key = CreateSourceDeclarationKey(
                        declaration.Namespace,
                        declaration.TypeNames
                    );
                    if (!locations.ContainsKey(key))
                    {
                        locations.Add(
                            key,
                            new SourceLocation(sourceFile.AssetPath, declaration.Line)
                        );
                    }
                }
            }
            return new MessageSourceIndexBuildResult(locations, isComplete);
        }

        private static void DrainMessageSourceAssemblyIndexes()
        {
            bool changed = false;
            foreach (MessageSourceAssemblyIndex index in MessageSourceAssemblyIndexes.Values)
            {
                if (index.BuildTask == null || !index.BuildTask.IsCompleted)
                {
                    continue;
                }

                try
                {
                    MessageSourceIndexBuildResult result = index.BuildTask.GetAwaiter().GetResult();
                    if (!result.IsComplete && index.Locations != null)
                    {
                        Dictionary<string, SourceLocation> mergedLocations = new(
                            index.Locations,
                            StringComparer.Ordinal
                        );
                        foreach (KeyValuePair<string, SourceLocation> pair in result.Locations)
                        {
                            mergedLocations[pair.Key] = pair.Value;
                        }
                        index.Locations = mergedLocations;
                    }
                    else
                    {
                        index.Locations = result.Locations;
                    }
                    index.IsComplete = result.IsComplete;
                    index.RetryAfterUtc = result.IsComplete
                        ? DateTime.MaxValue
                        : DateTime.UtcNow.AddSeconds(5);
                }
                catch (Exception exception)
                {
                    index.Locations = new Dictionary<string, SourceLocation>(
                        StringComparer.Ordinal
                    );
                    index.IsComplete = false;
                    index.RetryAfterUtc = DateTime.UtcNow.AddSeconds(5);
                    Debug.LogWarning(
                        $"DxMessaging could not index message source files: {exception.Message}"
                    );
                }
                index.BuildTask = null;
                changed = true;
            }

            if (changed)
            {
                MessageSourceIndexChanged?.Invoke();
            }

            if (!MessageSourceAssemblyIndexes.Values.Any(index => index.BuildTask != null))
            {
                EditorApplication.update -= DrainMessageSourceAssemblyIndexes;
            }
        }

        internal static void AllowIncompleteMessageSourceIndexRetries()
        {
            foreach (
                MessageSourceAssemblyIndex index in MessageSourceAssemblyIndexes.Values.Where(
                    index => index.BuildTask == null && !index.IsComplete
                )
            )
            {
                index.RetryAfterUtc = DateTime.MinValue;
            }
        }

        internal static int PendingMessageSourceIndexCount =>
            MessageSourceAssemblyIndexes.Values.Count(index => index.BuildTask != null);

        internal static int CompletedMessageSourceIndexCount =>
            MessageSourceAssemblyIndexes.Values.Count(index =>
                index.BuildTask != null && index.BuildTask.IsCompleted
            );

        internal static Func<string, string[]> MessageSourceFileReader
        {
            get => _messageSourceFileReader;
            set => _messageSourceFileReader = value ?? File.ReadAllLines;
        }

        internal static void ResetMessageSourceIndexesForTests()
        {
            EditorApplication.update -= DrainMessageSourceAssemblyIndexes;
            MessageSourceAssemblyIndexes.Clear();
            MessageSourceLocationCache.Clear();
            _messageSourceFileReader = File.ReadAllLines;
        }

        internal static void CompleteMessageSourceIndexesForTests()
        {
            Task[] buildTasks = MessageSourceAssemblyIndexes
                .Values.Where(index => index.BuildTask != null)
                .Select(index => (Task)index.BuildTask)
                .ToArray();
            Task.WaitAll(buildTasks);
            DrainMessageSourceAssemblyIndexes();
        }

        /// <summary>
        /// Raises the completion signal without needing a real index build, so a test can prove a
        /// window re-renders when the background index lands.
        /// </summary>
        internal static void RaiseMessageSourceIndexChangedForTests()
        {
            MessageSourceIndexChanged?.Invoke();
        }

        internal static void DrainMessageSourceIndexesForTests()
        {
            DrainMessageSourceAssemblyIndexes();
        }

        internal static int FindTypeDeclarationLine(
            IReadOnlyList<string> lines,
            string targetNamespace,
            IReadOnlyList<string> targetTypeNames
        )
        {
            if (lines == null || targetTypeNames == null || targetTypeNames.Count == 0)
            {
                return 0;
            }

            SourceTypeDeclaration match = FindSourceTypeDeclarations(lines)
                .FirstOrDefault(declaration =>
                    string.Equals(
                        declaration.Namespace,
                        targetNamespace ?? string.Empty,
                        StringComparison.Ordinal
                    )
                    && declaration.TypeNames.SequenceEqual(targetTypeNames, StringComparer.Ordinal)
                );
            return match.Line;
        }

        private static IReadOnlyList<SourceTypeDeclaration> FindSourceTypeDeclarations(
            IReadOnlyList<string> lines
        )
        {
            List<SourceTypeDeclaration> declarations = new();
            if (lines == null)
            {
                return declarations;
            }

            bool insideBlockComment = false;
            bool insideVerbatimString = false;
            string[] codeLines = new string[lines.Count];
            for (int index = 0; index < lines.Count; index++)
            {
                codeLines[index] = StripSourceCommentsAndLiterals(
                    lines[index] ?? string.Empty,
                    ref insideBlockComment,
                    ref insideVerbatimString
                );
            }

            List<SourceScope> scopes = new();
            string fileScopedNamespace = string.Empty;
            int braceDepth = 0;
            int attributeSquareDepth = 0;
            bool hasPendingScope = false;
            SourceScopeKind pendingScopeKind = SourceScopeKind.Type;
            string pendingScopeName = string.Empty;
            for (int index = 0; index < codeLines.Length; index++)
            {
                string code = codeLines[index];
                Match namespaceMatch = SourceNamespaceDeclarationPattern.Match(code);
                if (
                    namespaceMatch.Success
                    && string.Equals(namespaceMatch.Groups[2].Value, ";", StringComparison.Ordinal)
                )
                {
                    fileScopedNamespace = namespaceMatch.Groups[1].Value;
                    hasPendingScope = false;
                }
                else if (namespaceMatch.Success)
                {
                    hasPendingScope = true;
                    pendingScopeKind = SourceScopeKind.Namespace;
                    pendingScopeName = namespaceMatch.Groups[1].Value;
                }

                Match typeMatch = SourceTypeDeclarationPattern.Match(code);
                if (typeMatch.Success)
                {
                    string declaredTypeName = typeMatch.Groups[1].Value.TrimStart('@');
                    if (
                        code.Substring(typeMatch.Index + typeMatch.Length)
                            .TrimStart()
                            .StartsWith("<", StringComparison.Ordinal)
                    )
                    {
                        declaredTypeName += TryReadSourceGenericParameters(
                            codeLines,
                            index,
                            typeMatch.Index + typeMatch.Length,
                            out string genericParameters
                        )
                            ? "`" + CountSourceGenericParameters(genericParameters)
                            : "`?";
                    }
                    string currentNamespace = CreateSourceNamespace(fileScopedNamespace, scopes);
                    string[] currentTypeNames = scopes
                        .Where(scope => scope.Kind == SourceScopeKind.Type)
                        .Select(scope => scope.Name)
                        .Concat(new[] { declaredTypeName })
                        .ToArray();
                    declarations.Add(
                        new SourceTypeDeclaration(currentNamespace, currentTypeNames, index + 1)
                    );

                    hasPendingScope = true;
                    pendingScopeKind = SourceScopeKind.Type;
                    pendingScopeName = declaredTypeName;
                }

                foreach (char character in code)
                {
                    if (character == '[')
                    {
                        attributeSquareDepth++;
                        continue;
                    }
                    if (character == ']')
                    {
                        attributeSquareDepth = Math.Max(0, attributeSquareDepth - 1);
                        continue;
                    }
                    if (attributeSquareDepth > 0)
                    {
                        continue;
                    }
                    if (character == '{')
                    {
                        braceDepth++;
                        if (hasPendingScope)
                        {
                            scopes.Add(
                                new SourceScope(pendingScopeKind, pendingScopeName, braceDepth)
                            );
                            hasPendingScope = false;
                        }
                    }
                    else if (character == '}')
                    {
                        braceDepth = Math.Max(0, braceDepth - 1);
                        while (scopes.Count > 0 && scopes[scopes.Count - 1].BodyDepth > braceDepth)
                        {
                            scopes.RemoveAt(scopes.Count - 1);
                        }
                    }
                }

                if (hasPendingScope && code.IndexOf(';') >= 0)
                {
                    hasPendingScope = false;
                }
            }
            return declarations;
        }

        private static bool TryReadSourceGenericParameters(
            IReadOnlyList<string> codeLines,
            int declarationLine,
            int declarationEnd,
            out string genericParameters
        )
        {
            genericParameters = string.Empty;
            if (codeLines == null || declarationLine < 0 || declarationLine >= codeLines.Count)
            {
                return false;
            }

            StringBuilder parameters = new();
            bool started = false;
            int angleDepth = 0;
            int attributeSquareDepth = 0;
            for (int lineIndex = declarationLine; lineIndex < codeLines.Count; lineIndex++)
            {
                string line = codeLines[lineIndex] ?? string.Empty;
                int characterIndex = lineIndex == declarationLine ? declarationEnd : 0;
                for (; characterIndex < line.Length; characterIndex++)
                {
                    char character = line[characterIndex];
                    if (!started)
                    {
                        if (char.IsWhiteSpace(character))
                        {
                            continue;
                        }
                        if (character != '<')
                        {
                            return false;
                        }
                        started = true;
                        angleDepth = 1;
                        continue;
                    }

                    if (character == '[')
                    {
                        attributeSquareDepth++;
                    }
                    else if (character == ']')
                    {
                        attributeSquareDepth = Math.Max(0, attributeSquareDepth - 1);
                    }
                    else if (character == '<' && attributeSquareDepth == 0)
                    {
                        angleDepth++;
                    }
                    else if (character == '>' && attributeSquareDepth == 0)
                    {
                        angleDepth--;
                        if (angleDepth == 0)
                        {
                            genericParameters = parameters.ToString();
                            return !string.IsNullOrWhiteSpace(genericParameters);
                        }
                    }
                    parameters.Append(character);
                }
                if (started)
                {
                    parameters.Append(' ');
                }
            }
            return false;
        }

        private static IReadOnlyList<string> CreateSourceTypeNameChain(Type type)
        {
            List<string> names = new();
            for (Type current = type; current != null; current = current.DeclaringType)
            {
                names.Add(current.Name);
            }
            names.Reverse();
            return names;
        }

        private static int CountSourceGenericParameters(string genericParameters)
        {
            int count = 1;
            int squareDepth = 0;
            int parenthesisDepth = 0;
            for (int index = 0; index < genericParameters.Length; index++)
            {
                switch (genericParameters[index])
                {
                    case '[':
                        squareDepth++;
                        break;
                    case ']':
                        squareDepth = Math.Max(0, squareDepth - 1);
                        break;
                    case '(':
                        parenthesisDepth++;
                        break;
                    case ')':
                        parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
                        break;
                    case ',':
                        if (squareDepth == 0 && parenthesisDepth == 0)
                        {
                            count++;
                        }
                        break;
                }
            }
            return count;
        }

        private static string CreateSourceDeclarationKey(
            string sourceNamespace,
            IReadOnlyList<string> typeNames
        )
        {
            string typeName = string.Join("+", typeNames ?? Array.Empty<string>());
            return string.IsNullOrWhiteSpace(sourceNamespace)
                ? typeName
                : sourceNamespace + "." + typeName;
        }

        private static string CreateSourceNamespace(
            string fileScopedNamespace,
            IReadOnlyList<SourceScope> scopes
        )
        {
            return string.Join(
                ".",
                new[] { fileScopedNamespace ?? string.Empty }
                    .Concat(
                        scopes
                            .Where(scope => scope.Kind == SourceScopeKind.Namespace)
                            .Select(scope => scope.Name)
                    )
                    .Where(value => !string.IsNullOrWhiteSpace(value))
            );
        }

        private static string StripSourceCommentsAndLiterals(
            string line,
            ref bool insideBlockComment,
            ref bool insideVerbatimString
        )
        {
            StringBuilder code = new(line.Length);
            for (int index = 0; index < line.Length; index++)
            {
                if (insideVerbatimString)
                {
                    code.Append(' ');
                    if (line[index] != '"')
                    {
                        continue;
                    }
                    if (index + 1 < line.Length && line[index + 1] == '"')
                    {
                        code.Append(' ');
                        index++;
                        continue;
                    }

                    insideVerbatimString = false;
                    continue;
                }

                if (insideBlockComment)
                {
                    if (index + 1 < line.Length && line[index] == '*' && line[index + 1] == '/')
                    {
                        insideBlockComment = false;
                        code.Append("  ");
                        index++;
                    }
                    else
                    {
                        code.Append(' ');
                    }
                    continue;
                }

                if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '/')
                {
                    code.Append(' ', line.Length - index);
                    break;
                }
                if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '*')
                {
                    insideBlockComment = true;
                    code.Append("  ");
                    index++;
                    continue;
                }
                if (line[index] == '"' || line[index] == '\'')
                {
                    char quote = line[index];
                    bool verbatim =
                        quote == '"'
                        && index > 0
                        && (
                            line[index - 1] == '@'
                            || (line[index - 1] == '$' && index > 1 && line[index - 2] == '@')
                        );
                    code.Append(' ');
                    if (verbatim)
                    {
                        insideVerbatimString = true;
                        continue;
                    }
                    for (index++; index < line.Length; index++)
                    {
                        code.Append(' ');
                        if (line[index] == '\\' && index + 1 < line.Length)
                        {
                            code.Append(' ');
                            index++;
                            continue;
                        }
                        if (line[index] == quote)
                        {
                            break;
                        }
                    }
                    continue;
                }

                code.Append(line[index]);
            }
            return code.ToString();
        }

        internal static CapturedTypeIdentity ParseCapturedTypeIdentity(string typeName)
        {
            string captured = typeName ?? string.Empty;
            int assemblyStart = captured.LastIndexOf(" [", StringComparison.Ordinal);
            if (assemblyStart < 0 || !captured.EndsWith("]", StringComparison.Ordinal))
            {
                return new CapturedTypeIdentity(captured, string.Empty);
            }

            string assemblyName = captured.Substring(
                assemblyStart + 2,
                captured.Length - assemblyStart - 3
            );
            return string.IsNullOrWhiteSpace(assemblyName)
                ? new CapturedTypeIdentity(captured, string.Empty)
                : new CapturedTypeIdentity(captured.Substring(0, assemblyStart), assemblyName);
        }

        internal static Button CreateSourceLinkButton(
            string label,
            SourceLocation location,
            bool includeLocationInText = true
        )
        {
            Button button = new()
            {
                name = SourceLinkButtonName,
                text = includeLocationInText
                    ? $"{label} - {Path.GetFileName(location.AssetPath)}:{location.Line}"
                    : label,
                tooltip = $"Open {location.AssetPath}:{location.Line}",
                userData = location,
            };
            button.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            button.style.marginLeft = 6;
            button.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                OpenSourceLocation(location);
            });
            return button;
        }

        internal static void OpenSourceLocation(SourceLocation location)
        {
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(location.AssetPath);
            if (asset == null)
            {
                return;
            }

            EditorGUIUtility.PingObject(asset);
            _ = AssetDatabase.OpenAsset(asset, location.Line);
        }

        /// <summary>
        /// Rewrites an <see cref="Type.AssemblyQualifiedName"/> into the
        /// <c>Namespace.Type [Assembly]</c> shape <see cref="TryResolveMessageSource"/> reads.
        /// The split is the first comma AFTER the last <c>]</c> rather than the first comma in
        /// the string, because a constructed generic carries its own comma-separated argument
        /// assemblies inside brackets and splitting on those yields a type name that resolves
        /// to nothing.
        /// </summary>
        internal static string CreateSourceLookupName(string assemblyQualifiedName)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
            {
                return string.Empty;
            }

            int searchFrom = assemblyQualifiedName.LastIndexOf(']') + 1;
            int assemblySeparator = assemblyQualifiedName.IndexOf(',', searchFrom);
            if (assemblySeparator < 0)
            {
                return assemblyQualifiedName.Trim();
            }

            string typeName = assemblyQualifiedName.Substring(0, assemblySeparator).Trim();
            string remainder = assemblyQualifiedName.Substring(assemblySeparator + 1);
            int nameEnd = remainder.IndexOf(',');
            string assemblyName = (
                nameEnd < 0 ? remainder : remainder.Substring(0, nameEnd)
            ).Trim();
            return string.IsNullOrEmpty(assemblyName) ? typeName : $"{typeName} [{assemblyName}]";
        }

        /// <summary>
        /// Resolves the declaring source file of a type captured as an assembly-qualified name,
        /// which is the shape the Message Monitor records.
        /// </summary>
        internal static bool TryResolveSourceForAssemblyQualifiedName(
            string assemblyQualifiedName,
            out SourceLocation location
        )
        {
            return TryResolveMessageSource(
                CreateSourceLookupName(assemblyQualifiedName),
                out location
            );
        }

        /// <summary>
        /// Makes <paramref name="element"/> behave like the link it looks like: clickable,
        /// reachable and activatable from the keyboard, and carrying the pointer cursor that
        /// tells a reader it can be clicked at all. This is the single definition of "this is
        /// interactive" for the diagnostics windows, so an element that skips it does not get
        /// the affordance by accident and one that takes it cannot forget a piece of it.
        /// </summary>
        internal static void MakeActivatable(
            VisualElement element,
            string tooltip,
            Action onActivate,
            bool addLinkClass = true
        )
        {
            if (element == null || onActivate == null)
            {
                return;
            }

            if (addLinkClass)
            {
                element.AddToClassList(DxMessagingEditorTheme.DetailLinkClassName);
            }
            element.AddToClassList(DxMessagingEditorTheme.ClickableClassName);
            element.focusable = true;
            element.pickingMode = PickingMode.Position;
            if (!string.IsNullOrWhiteSpace(tooltip))
            {
                element.tooltip = tooltip;
            }
            element.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                onActivate();
            });
            element.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (!IsActivationKey(evt))
                {
                    return;
                }

                evt.StopPropagation();
                onActivate();
            });
        }

        /// <summary>
        /// Whether a key event means "activate this". The key code ONLY, deliberately: UI Toolkit
        /// raises two `KeyDownEvent`s for Return and Space -- one carrying the key code, then one
        /// carrying the character with `KeyCode.None` -- so also accepting the character runs the
        /// activation twice per keypress on any element that keeps focus. Entering live mode or
        /// pinging a context twice from one press is worse than the version-portability the
        /// character branch was reaching for, and that portability turned out to be unnecessary:
        /// the 2022.3 failure it was added for was a test dispatching at the element instead of
        /// through the panel.
        /// </summary>
        private static bool IsActivationKey(KeyDownEvent evt)
        {
            return evt.keyCode == KeyCode.Return
                || evt.keyCode == KeyCode.KeypadEnter
                || evt.keyCode == KeyCode.Space;
        }

        /// <summary>
        /// Resolves the Unity object a captured context id stood for, or null once that object
        /// is gone. A destroyed context is the normal case for a log that outlives the scene
        /// it recorded, so callers use this to decide whether a row can link at all rather
        /// than offering a link that does nothing.
        /// </summary>
        /// <remarks>
        /// Editor-only, and deliberately the int overload. <see cref="InstanceId"/> stores the
        /// same 32-bit key the runtime uses; see the accessor note on
        /// <see cref="InstanceId.StableId"/>, which reads that key from <c>EntityId</c> on Unity
        /// 6.4+ because the legacy accessor it replaced is being retired. This lookup is the
        /// inverse of the same key and runs only in the editor, so it is not on the path that
        /// note protects. If a future editor version retires the int overload too, this is the
        /// single place that has to move.
        ///
        /// The legacy accessor is deliberately not named here: `InstanceIdSourceTests` text-scans
        /// shipped source for it, and `Runtime/Core/InstanceId.cs` is the only file allowed to
        /// mention it at all.
        /// </remarks>
        internal static UnityEngine.Object FindContextObject(int contextInstanceId)
        {
            return contextInstanceId == 0
                ? null
                : EditorUtility.InstanceIDToObject(contextInstanceId);
        }

        /// <summary>
        /// Selects and pings the object a captured context id stood for, so the reader lands on
        /// it in the Hierarchy (or the Project window) with the Inspector already showing it.
        /// </summary>
        internal static bool TryRevealContext(int contextInstanceId)
        {
            UnityEngine.Object contextObject = FindContextObject(contextInstanceId);
            if (contextObject == null)
            {
                return false;
            }

            Selection.activeObject = contextObject;
            EditorGUIUtility.PingObject(contextObject);
            return true;
        }
    }
}
#endif
