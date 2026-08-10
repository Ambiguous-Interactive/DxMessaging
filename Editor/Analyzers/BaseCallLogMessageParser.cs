#nullable enable annotations
namespace DxMessaging.Editor.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Single parsed diagnostic line emitted by the <c>MessageAwareComponentBaseCallAnalyzer</c>.
    /// </summary>
    /// <remarks>
    /// Empty <see cref="MethodName"/> is the documented sentinel for DXMSG008 (which carries no
    /// method name in its message format). Empty <see cref="FilePath"/> / zero <see cref="Line"/>
    /// indicate the bare (Unity-prefix-less) form of the message.
    /// </remarks>
#if UNITY_EDITOR
    public
#else
    internal
#endif
    readonly struct ParsedEntry
    {
        public string DiagnosticId { get; }

        public string TypeFullName { get; }

        public string MethodName { get; }

        public string FilePath { get; }

        public int Line { get; }

        public ParsedEntry(
            string diagnosticId,
            string typeFullName,
            string methodName,
            string filePath,
            int line
        )
        {
            DiagnosticId = diagnosticId;
            TypeFullName = typeFullName;
            MethodName = methodName;
            FilePath = filePath;
            Line = line;
        }
    }

    /// <summary>
    /// Per-type aggregate produced by <see cref="BaseCallLogMessageParser.Aggregate"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="MissingBaseFor"/> is deduplicated and stored in deterministic ordinal-sorted order
    /// via <see cref="SortedSet{T}"/>. <see cref="DiagnosticIds"/> uses ordinal comparison.
    /// <see cref="FilePath"/> / <see cref="Line"/> hold the FIRST seen non-empty values so the
    /// inspector overlay's "Open Script" jump remains stable across repeated parses.
    /// </remarks>
#if UNITY_EDITOR
    public
#else
    internal
#endif
    sealed class ParsedTypeReport
    {
        public string TypeFullName { get; set; } = string.Empty;

        public SortedSet<string> MissingBaseFor { get; } = new(StringComparer.Ordinal);

        public HashSet<string> DiagnosticIds { get; } = new(StringComparer.Ordinal);

        public string FilePath { get; set; } = string.Empty;

        public int Line { get; set; }
    }

    /// <summary>
    /// Pure parser for the DXMSG006/DXMSG007/DXMSG008/DXMSG009/DXMSG010 console-log lines emitted
    /// by the <c>MessageAwareComponentBaseCallAnalyzer</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class lives in the Editor assembly so the harvester can call it directly without
    /// referencing the Roslyn-analyzer DLL (Unity excludes <c>RoslynAnalyzer</c>-labelled
    /// assemblies from asmdef compile-time references).
    /// </para>
    /// <para>
    /// The analyzer-tests project compiles its own copy of this file via a
    /// <c>&lt;Compile Include="...\BaseCallLogMessageParser.cs" Link="..." /&gt;</c> so the
    /// existing dotnet-test coverage continues to run.
    /// </para>
    /// <para>
    /// The regexes are pinned to the analyzer's verbatim message-format strings. Whenever those
    /// formats change, both this parser AND <c>BaseCallLogMessageParserTests</c> must be updated
    /// in lockstep.
    /// </para>
    /// </remarks>
#if UNITY_EDITOR
    public
#else
    internal
#endif
    static class BaseCallLogMessageParser
    {
        // Roslyn / Unity-style location prefix:  path(line,col): warning DXMSG006:
        // We don't anchor to the diagnostic id here beyond the leading "DXMSG"; that lets the
        // same prefix regex serve all five diagnostics (DXMSG006/007/008/009/010). The trailing
        // `: ` is consumed so the diagnostic-specific regexes only see the message body.
        private const RegexOptions SharedOptions =
            RegexOptions.Compiled | RegexOptions.CultureInvariant;

        private static readonly Regex PrefixRegex = new(
            @"^(?<path>[^()\r\n]+?)\((?<line>\d+),(?<col>\d+)\)\s*:\s*(?:warning|error|info|hidden|message)\s+DXMSG0(?:0[6789]|10)\s*:\s*",
            SharedOptions
        );

        // DXMSG006 format:
        //   '{type}' overrides MessageAwareComponent.{method} but does not call base.{method}();
        //   the messaging system may not function correctly on this component.
        // The type name is captured from the first single-quoted token; the method from the first
        // `MessageAwareComponent.{method}` occurrence (the format string repeats `{method}`).
        // Body regexes anchor to the start of the body (^) so a Debug.Log payload that *contains*
        // the analyzer's wording mid-string is not surfaced as a real DXMSG006/007/008. The prefix
        // (when present) is stripped before this match, so ^ here is the start of the message body.
        private static readonly Regex Dxmsg006Regex = new(
            @"^'(?<type>[^']+)'\s+overrides\s+MessageAwareComponent\.(?<method>[A-Za-z_][A-Za-z0-9_]*)\s+but\s+does\s+not\s+call\s+base\.[A-Za-z_][A-Za-z0-9_]*\(\)\s*;\s*the\s+messaging\s+system\s+may\s+not\s+function\s+correctly\s+on\s+this\s+component\.",
            SharedOptions
        );

        // DXMSG007 format:
        //   '{type}' hides MessageAwareComponent.{method} with 'new'; replace with 'override' and
        //   call base.{method}() so the messaging system continues to function.
        private static readonly Regex Dxmsg007Regex = new(
            @"^'(?<type>[^']+)'\s+hides\s+MessageAwareComponent\.(?<method>[A-Za-z_][A-Za-z0-9_]*)\s+with\s+'new'\s*;\s*replace\s+with\s+'override'\s+and\s+call\s+base\.[A-Za-z_][A-Za-z0-9_]*\(\)\s+so\s+the\s+messaging\s+system\s+continues\s+to\s+function\.",
            SharedOptions
        );

        // DXMSG008 format:
        //   '{type}' is excluded from the DxMessaging base-call check ({source}).
        // No method name in the message; MethodName is returned as the empty string.
        private static readonly Regex Dxmsg008Regex = new(
            @"^'(?<type>[^']+)'\s+is\s+excluded\s+from\s+the\s+DxMessaging\s+base-call\s+check\s+\([^)]*\)\.",
            SharedOptions
        );

        // DXMSG009 format:
        //   '{type}' declares {method} without 'override' or 'new'; this implicitly hides
        //   MessageAwareComponent.{method} (CS0114) and the messaging system will not function. ...
        // We anchor on the head of the message and stop after the modifier-tokens phrase so future
        // wording tweaks to the trailing remediation text don't break the parser.
        private static readonly Regex Dxmsg009Regex = new(
            @"^'(?<type>[^']+)'\s+declares\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s+without\s+'override'\s+or\s+'new'",
            SharedOptions
        );

        // DXMSG010 format:
        //   '{type}' calls base.{method}() but the inherited override on '{broken}' does not
        //   chain to MessageAwareComponent.{method}; the messaging system will not function
        //   correctly on this component.
        // We capture {type} (the class the user is editing), {method}, and the broken-ancestor
        // FQN so the inspector overlay can mention "broken chain via {broken}" if desired.
        private static readonly Regex Dxmsg010Regex = new(
            @"^'(?<type>[^']+)'\s+calls\s+base\.(?<method>[A-Za-z_][A-Za-z0-9_]*)\(\)\s+but\s+the\s+inherited\s+override\s+on\s+'(?<broken>[^']+)'",
            SharedOptions
        );

        /// <summary>
        /// Parses one console log line. Returns <c>null</c> if the line is not a recognised
        /// DXMSG006/DXMSG007/DXMSG008/DXMSG009/DXMSG010 message.
        /// </summary>
        /// <remarks>
        /// Tolerates both the Roslyn-prefixed form (<c>Path(L,C): warning DXMSG006: ...</c>) and the
        /// bare form (just the message body). The prefix's path/line are captured; the bare form
        /// returns empty path / zero line.
        /// </remarks>
        public static ParsedEntry? ParseLine(string logLine)
        {
            if (string.IsNullOrWhiteSpace(logLine))
            {
                return null;
            }

            string filePath = string.Empty;
            int line = 0;
            string body = logLine;

            Match prefixMatch = PrefixRegex.Match(logLine);
            if (prefixMatch.Success)
            {
                filePath = prefixMatch.Groups["path"].Value;
                if (
                    !int.TryParse(
                        prefixMatch.Groups["line"].Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out line
                    )
                )
                {
                    line = 0;
                }
                body = logLine.Substring(prefixMatch.Length);
            }

            Match dxmsg006 = Dxmsg006Regex.Match(body);
            if (dxmsg006.Success)
            {
                string type = dxmsg006.Groups["type"].Value;
                string method = dxmsg006.Groups["method"].Value;
                if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(method))
                {
                    return new ParsedEntry("DXMSG006", type, method, filePath, line);
                }
            }

            Match dxmsg007 = Dxmsg007Regex.Match(body);
            if (dxmsg007.Success)
            {
                string type = dxmsg007.Groups["type"].Value;
                string method = dxmsg007.Groups["method"].Value;
                if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(method))
                {
                    return new ParsedEntry("DXMSG007", type, method, filePath, line);
                }
            }

            Match dxmsg008 = Dxmsg008Regex.Match(body);
            if (dxmsg008.Success)
            {
                string type = dxmsg008.Groups["type"].Value;
                if (!string.IsNullOrWhiteSpace(type))
                {
                    return new ParsedEntry("DXMSG008", type, string.Empty, filePath, line);
                }
            }

            Match dxmsg009 = Dxmsg009Regex.Match(body);
            if (dxmsg009.Success)
            {
                string type = dxmsg009.Groups["type"].Value;
                string method = dxmsg009.Groups["method"].Value;
                if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(method))
                {
                    return new ParsedEntry("DXMSG009", type, method, filePath, line);
                }
            }

            Match dxmsg010 = Dxmsg010Regex.Match(body);
            if (dxmsg010.Success)
            {
                string type = dxmsg010.Groups["type"].Value;
                string method = dxmsg010.Groups["method"].Value;
                if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(method))
                {
                    return new ParsedEntry("DXMSG010", type, method, filePath, line);
                }
            }

            return null;
        }

        /// <summary>
        /// Aggregates many log lines into a per-type report keyed by FQN. Deduplicates methods and
        /// diagnostic ids ordinally; method names are stored in deterministic ordinal-sorted order.
        /// Preserves first-occurrence for <see cref="ParsedTypeReport.FilePath"/> /
        /// <see cref="ParsedTypeReport.Line"/>.
        /// </summary>
        public static Dictionary<string, ParsedTypeReport> Aggregate(IEnumerable<string>? logLines)
        {
            Dictionary<string, ParsedTypeReport> result = new(StringComparer.Ordinal);
            if (logLines == null)
            {
                return result;
            }

            foreach (string logLine in logLines)
            {
                ParsedEntry? parsed = ParseLine(logLine);
                if (parsed is not ParsedEntry entry)
                {
                    continue;
                }

                if (!result.TryGetValue(entry.TypeFullName, out ParsedTypeReport? report))
                {
                    report = new ParsedTypeReport { TypeFullName = entry.TypeFullName };
                    result[entry.TypeFullName] = report;
                }

                report.DiagnosticIds.Add(entry.DiagnosticId);

                if (!string.IsNullOrEmpty(entry.MethodName))
                {
                    report.MissingBaseFor.Add(entry.MethodName);
                }

                if (string.IsNullOrEmpty(report.FilePath) && !string.IsNullOrEmpty(entry.FilePath))
                {
                    report.FilePath = entry.FilePath;
                    report.Line = entry.Line;
                }
            }

            return result;
        }
    }
}
