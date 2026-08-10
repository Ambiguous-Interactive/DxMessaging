namespace WallstopStudios.DxMessaging.SourceGenerators.Analyzers
{
    using System;
    using System.Collections.Immutable;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Text;

    /// <summary>
    /// Loads and caches the per-project base-call ignore list shipped to the analyzer via
    /// <see cref="AnalyzerOptions.AdditionalFiles"/>.
    /// </summary>
    /// <remarks>
    /// The sidecar file is auto-generated from <c>DxMessagingSettings.asset</c> by the Editor
    /// integration. This reader is tolerant of missing files, blank lines, surrounding whitespace,
    /// <c>#</c>-style comments, and an optional <c>global::</c> prefix on each entry (J in the
    /// adversarial review -- keeps the FQN comparison friendly to copy-paste from compiler output).
    /// <para>
    /// Results are cached per <see cref="AnalyzerOptions"/> instance via a
    /// <see cref="ConditionalWeakTable{TKey, TValue}"/> + <see cref="Lazy{T}"/> pair so repeat callbacks
    /// do not re-parse the file. The <see cref="Lazy{T}"/> wrapper provides single-shot per-instance
    /// memoization without a racy try/Add/catch dance.
    /// </para>
    /// <para>
    /// IDE-reuse caveat: under incremental scenarios (Roslyn workspace edits, IDE typing) the host
    /// may construct a fresh <see cref="AnalyzerOptions"/> instance per snapshot. The cache is
    /// keyed on identity, so a new instance simply re-parses on first Load -- correct behaviour,
    /// just not maximally cached. Within the same options instance, only one parse ever runs.
    /// </para>
    /// </remarks>
    internal static class IgnoreListReader
    {
        internal const string IgnoreFileName = "DxMessaging.BaseCallIgnore.txt";

        private const string GlobalPrefix = "global::";

        private static readonly char[] DirectorySeparators = { '/', '\\' };

        private static readonly ConditionalWeakTable<
            AnalyzerOptions,
            Lazy<ImmutableHashSet<string>>
        > Cache = new();

        internal static ImmutableHashSet<string> Load(
            AnalyzerOptions options,
            CancellationToken cancellationToken
        )
        {
            if (options is null)
            {
                return ImmutableHashSet<string>.Empty;
            }

            // GetValue returns the existing entry or creates one atomically using the factory.
            // Lazy<T> ensures a single parse per options instance even under thread contention.
            //
            // S1. We deliberately pass CancellationToken.None to the factory rather than the
            // outer Load call's token. With LazyThreadSafetyMode.ExecutionAndPublication, the
            // first caller's token is baked into the closure and any OperationCanceledException
            // it throws gets cached forever and rethrown for every subsequent caller using the
            // same AnalyzerOptions. The parse work is bounded by the size of one small text
            // file, so dropping cancellation here is acceptable; the outer `cancellationToken`
            // parameter still flows through symbol-side lookups in the analyzer call sites.
            Lazy<ImmutableHashSet<string>> lazy = Cache.GetValue(
                options,
                key => new Lazy<ImmutableHashSet<string>>(
                    () => Parse(key, CancellationToken.None),
                    System.Threading.LazyThreadSafetyMode.ExecutionAndPublication
                )
            );

            return lazy.Value;
        }

        private static ImmutableHashSet<string> Parse(
            AnalyzerOptions options,
            CancellationToken cancellationToken
        )
        {
            ImmutableHashSet<string>.Builder builder = ImmutableHashSet.CreateBuilder<string>(
                StringComparer.Ordinal
            );

            foreach (AdditionalText additionalText in options.AdditionalFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (additionalText is null)
                {
                    continue;
                }

                string fileName = GetFileName(additionalText.Path);
                if (!string.Equals(fileName, IgnoreFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SourceText? sourceText = additionalText.GetText(cancellationToken);
                if (sourceText is null)
                {
                    continue;
                }

                foreach (TextLine line in sourceText.Lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string raw = line.ToString();
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    string trimmed = raw.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }

                    // J. Friendly UX: strip every leading `global::` so users can paste FQNs
                    // directly from compiler diagnostics (which often emit the global:: prefix)
                    // without manual editing. Loop instead of branching once so a pathological
                    // `global::global::Foo` (won't compile, but cheap to handle) collapses
                    // correctly. The analyzer always compares against an FQN with the global
                    // namespace style omitted.
                    while (
                        trimmed.StartsWith(GlobalPrefix, StringComparison.Ordinal)
                        && trimmed.Length > GlobalPrefix.Length
                    )
                    {
                        trimmed = trimmed.Substring(GlobalPrefix.Length);
                    }

                    builder.Add(trimmed);
                }
            }

            return builder.ToImmutable();
        }

        private static string GetFileName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            int lastSlash = path.LastIndexOfAny(DirectorySeparators);
            return lastSlash < 0 ? path : path.Substring(lastSlash + 1);
        }
    }
}
