#nullable enable annotations
namespace DxMessaging.Editor.Analyzers
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Pure-BCL data-transfer object for an aggregated per-FQN report row. Mirrors the public
    /// shape of <see cref="BaseCallReportEntry"/> but uses property-style PascalCase fields so it
    /// does not depend on Unity's <c>JsonUtility</c> serialization conventions and is safely
    /// constructible from <c>dotnet test</c>.
    /// </summary>
    /// <remarks>
    /// The harvester wraps every DTO in a <see cref="BaseCallReportEntry"/> (with the lowercase
    /// Unity-serialisable field names) before the snapshot crosses into Editor code. Keep this
    /// shape in lock-step with that wrapper -- fields added here must also flow through to the
    /// Unity-facing entry or the inspector overlay won't see them.
    /// </remarks>
#if UNITY_EDITOR
    public
#else
    internal
#endif
    sealed class BaseCallReportEntryDto
    {
        /// <summary>Fully-qualified name of the offending type (dot-form for nested types).</summary>
        public string TypeName = string.Empty;

        /// <summary>Method names whose overrides are missing the corresponding <c>base.*()</c> call.</summary>
        public readonly SortedSet<string> MissingBaseFor = new(StringComparer.Ordinal);

        /// <summary>Diagnostic IDs that contributed to this entry (e.g., DXMSG006/007/008/009).</summary>
        public readonly HashSet<string> DiagnosticIds = new(StringComparer.Ordinal);

        /// <summary>Source file path (best-effort) for "Open Script" actions in the inspector overlay.</summary>
        public string FilePath = string.Empty;

        /// <summary>1-based line number of the first relevant diagnostic, when known.</summary>
        public int Line;
    }

    /// <summary>
    /// Merges Console and assembly compilation diagnostics into per-type report rows.
    /// </summary>
#if UNITY_EDITOR
    public
#else
    internal
#endif
    static class BaseCallReportAggregator
    {
        /// <summary>
        /// Applies a single assembly's freshly parsed batch to its type-name membership and
        /// refreshes the combined per-type report.
        /// </summary>
        /// <remarks>
        /// An assembly's earlier diagnostic details may remain while another assembly still
        /// reports the same type name. The inputs carry one combined payload per type.
        /// </remarks>
        /// <param name="assemblyKey">Stable identifier for the source assembly (typically the
        /// assembly path Unity passes via <c>CompilationPipeline.assemblyCompilationFinished</c>).</param>
        /// <param name="latestReportsForAssembly">Reports parsed from this assembly's most recent
        /// compilation. May be empty -- that case is the retirement path (every FQN this assembly
        /// previously reported is dropped).</param>
        /// <param name="typesByAssembly">Per-assembly FQN bookkeeping. Mutated in place.</param>
        /// <param name="mergedReports">Combined diagnostic payload per type. Mutated in place.</param>
        public static void ApplyAssemblyReports(
            string assemblyKey,
            IReadOnlyDictionary<string, ParsedTypeReport>? latestReportsForAssembly,
            Dictionary<string, HashSet<string>> typesByAssembly,
            Dictionary<string, ParsedTypeReport> mergedReports
        )
        {
            if (string.IsNullOrEmpty(assemblyKey))
            {
                throw new ArgumentException("Assembly key must be non-empty.", nameof(assemblyKey));
            }
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(typesByAssembly);
            ArgumentNullException.ThrowIfNull(mergedReports);
#else
            if (typesByAssembly is null)
            {
                throw new ArgumentNullException(nameof(typesByAssembly));
            }
            if (mergedReports is null)
            {
                throw new ArgumentNullException(nameof(mergedReports));
            }
#endif

            /*
                1. Replace this assembly's FQN set with the latest batch. Types absent from the new
                   batch are dropped from the assembly's row; that's the per-assembly retirement.
            */
            if (!typesByAssembly.TryGetValue(assemblyKey, out HashSet<string>? typeSet))
            {
                typeSet = new HashSet<string>(StringComparer.Ordinal);
                typesByAssembly[assemblyKey] = typeSet;
            }
            typeSet.Clear();
            if (latestReportsForAssembly is not null)
            {
                foreach (string fqn in latestReportsForAssembly.Keys)
                {
                    if (!string.IsNullOrEmpty(fqn))
                    {
                        typeSet.Add(fqn);
                    }
                }
            }

            /*
                2. Rebuild mergedReports for the remaining type memberships. If another assembly
                   still reports a type, its diagnostic details come from the prior combined payload.

                   Per-FQN merge semantics:
                   - Method list: union, deduplicated ordinally, stored in deterministic
                     ordinal-sorted order.
                   - Diagnostic IDs: union via HashSet.
                   - File path / line: first non-empty wins (stable across recompiles).
            */
            Dictionary<string, ParsedTypeReport> rebuilt = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, HashSet<string>> assemblyEntry in typesByAssembly)
            {
                string thisAssemblyKey = assemblyEntry.Key;
                HashSet<string> fqns = assemblyEntry.Value;
                if (fqns is null || fqns.Count == 0)
                {
                    continue;
                }

                /*
                    For the assembly we just updated, prefer the freshly-parsed payload. For other
                    assemblies, we need the previous merge to still carry their data; but that
                    information is only retrievable from the OUTGOING mergedReports, so we read it
                    before clearing.
                */
                IReadOnlyDictionary<string, ParsedTypeReport>? source = string.Equals(
                    thisAssemblyKey,
                    assemblyKey,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? latestReportsForAssembly
                    : mergedReports;
                if (source is null)
                {
                    continue;
                }

                foreach (string fqn in fqns)
                {
                    if (!source.TryGetValue(fqn, out ParsedTypeReport? contribution))
                    {
                        continue;
                    }
                    if (contribution is null)
                    {
                        continue;
                    }

                    if (!rebuilt.TryGetValue(fqn, out ParsedTypeReport? existing))
                    {
                        /*
                            Defensive copy so future ApplyAssemblyReports calls don't mutate state
                            that callers may still hold a reference to.
                        */
                        existing = new ParsedTypeReport
                        {
                            TypeFullName = contribution.TypeFullName,
                            FilePath = contribution.FilePath,
                            Line = contribution.Line,
                        };
                        foreach (string method in contribution.MissingBaseFor)
                        {
                            if (!string.IsNullOrEmpty(method))
                            {
                                existing.MissingBaseFor.Add(method);
                            }
                        }
                        foreach (string id in contribution.DiagnosticIds)
                        {
                            existing.DiagnosticIds.Add(id);
                        }
                        rebuilt[fqn] = existing;
                        continue;
                    }

                    foreach (string method in contribution.MissingBaseFor)
                    {
                        if (!string.IsNullOrEmpty(method))
                        {
                            existing.MissingBaseFor.Add(method);
                        }
                    }
                    foreach (string id in contribution.DiagnosticIds)
                    {
                        existing.DiagnosticIds.Add(id);
                    }
                    if (
                        string.IsNullOrEmpty(existing.FilePath)
                        && !string.IsNullOrEmpty(contribution.FilePath)
                    )
                    {
                        existing.FilePath = contribution.FilePath;
                        existing.Line = contribution.Line;
                    }
                }
            }

            mergedReports.Clear();
            foreach (KeyValuePair<string, ParsedTypeReport> kvp in rebuilt)
            {
                mergedReports[kvp.Key] = kvp.Value;
            }
        }

        /// <summary>
        /// Builds the final flat snapshot for the inspector overlay by unioning the LogEntries
        /// scan with the per-assembly merged reports. Both inputs are read-only; the result is a
        /// fresh dictionary the caller owns.
        /// </summary>
        /// <param name="logEntriesReports">Reports harvested from <c>UnityEditor.LogEntries</c>.
        /// May be empty (Unity 2021 path) or null (LogEntries reflection unavailable).</param>
        /// <param name="mergedReports">Per-FQN merged view of every assembly's latest reports
        /// produced by <see cref="ApplyAssemblyReports"/>. May be empty when no compilation
        /// callbacks have fired yet.</param>
        public static Dictionary<string, BaseCallReportEntryDto> BuildSnapshot(
            IReadOnlyDictionary<string, ParsedTypeReport>? logEntriesReports,
            IReadOnlyDictionary<string, ParsedTypeReport>? mergedReports
        )
        {
            Dictionary<string, BaseCallReportEntryDto> snapshot = new(StringComparer.Ordinal);
            MergeReports(snapshot, logEntriesReports);
            MergeReports(snapshot, mergedReports);
            return snapshot;
        }

        private static void MergeReports(
            Dictionary<string, BaseCallReportEntryDto> snapshot,
            IReadOnlyDictionary<string, ParsedTypeReport>? reports
        )
        {
            if (reports is null)
            {
                return;
            }
            foreach (KeyValuePair<string, ParsedTypeReport> report in reports)
            {
                AddOrMerge(snapshot, report.Key, report.Value);
            }
        }

        /// <summary>Keeps the latest compiler diagnostics for each reporting assembly.</summary>
        internal sealed class CompilerReports
        {
            private readonly List<(
                string AssemblyPath,
                IReadOnlyDictionary<string, ParsedTypeReport> Reports
            )> _reports = new();

            /// <summary>Replaces the assembly's report while preserving active assembly order.</summary>
            internal void Replace(
                string assemblyPath,
                IReadOnlyDictionary<string, ParsedTypeReport>? reports
            )
            {
                for (int index = 0; index < _reports.Count; index++)
                {
                    if (
                        !string.Equals(
                            _reports[index].AssemblyPath,
                            assemblyPath,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        continue;
                    }

                    if (reports is null || reports.Count == 0)
                    {
                        _reports.RemoveAt(index);
                    }
                    else
                    {
                        _reports[index] = (assemblyPath, reports);
                    }
                    return;
                }

                if (reports is { Count: > 0 })
                {
                    _reports.Add((assemblyPath, reports));
                }
            }

            /// <summary>Releases the reports for all assemblies.</summary>
            internal void Clear() => _reports.Clear();

            /// <summary>Unions Console rows and the latest reports in their active order.</summary>
            internal Dictionary<string, BaseCallReportEntryDto> BuildSnapshot(
                IReadOnlyDictionary<string, ParsedTypeReport>? logEntriesReports
            )
            {
                Dictionary<string, BaseCallReportEntryDto> snapshot = new(StringComparer.Ordinal);
                MergeReports(snapshot, logEntriesReports);
                foreach (
                    (string _, IReadOnlyDictionary<string, ParsedTypeReport> reports) in _reports
                )
                {
                    MergeReports(snapshot, reports);
                }
                return snapshot;
            }
        }

        private static void AddOrMerge(
            Dictionary<string, BaseCallReportEntryDto> snapshot,
            string fqn,
            ParsedTypeReport report
        )
        {
            if (string.IsNullOrEmpty(fqn) || report is null)
            {
                return;
            }

            if (!snapshot.TryGetValue(fqn, out BaseCallReportEntryDto? existing))
            {
                existing = new BaseCallReportEntryDto { TypeName = fqn };
                snapshot[fqn] = existing;
            }
            if (string.IsNullOrEmpty(existing.TypeName))
            {
                existing.TypeName = fqn;
            }

            foreach (string method in report.MissingBaseFor)
            {
                if (!string.IsNullOrEmpty(method))
                {
                    /*
                        Dedupe across the dual-source merge: LogEntries and CompilerMessage may
                        both surface the same `<type>.<method>` pair on Unity 2022+, where both
                        pipes are wired. MissingBaseFor is a SortedSet<string>, so duplicates are
                        removed and HelpBox output remains stable via deterministic ordinal sorting.
                    */
                    existing.MissingBaseFor.Add(method);
                }
            }

            foreach (string id in report.DiagnosticIds)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    /*
                        HashSet dedupes repeated IDs surfaced by the dual-source merge
                        (LogEntries + CompilerMessage on Unity 2022+).
                    */
                    existing.DiagnosticIds.Add(id);
                }
            }

            /*
                First seen file/line wins so "Open Script" jumps to a stable location across
                rebuilds; which is what the user's eye lands on first in the console.
            */
            if (string.IsNullOrEmpty(existing.FilePath) && !string.IsNullOrEmpty(report.FilePath))
            {
                existing.FilePath = report.FilePath;
                existing.Line = report.Line;
            }
        }
    }
}
