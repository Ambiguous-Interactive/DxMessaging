#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Runtime.InteropServices;
    using System.Text;
    using UnityEngine;

    internal readonly struct BenchmarkEntry
    {
        internal BenchmarkEntry(string messageTech, long operationsPerSecond, bool allocating)
        {
            MessageTech = messageTech;
            OperationsPerSecond = operationsPerSecond;
            Allocating = allocating;
        }

        internal string MessageTech { get; }

        internal long OperationsPerSecond { get; }

        internal bool Allocating { get; }
    }

    /// <summary>
    /// Local-only diagnostic for the legacy editor benchmark suite. It logs each
    /// recorded benchmark row to the Unity console as it arrives and, at dispose
    /// time, emits the assembled markdown table as a single labeled block. This
    /// session never reads or writes any documentation: the canonical perf tables
    /// are owned solely by the CI renderer (<c>scripts/unity/render-perf-doc.js</c>).
    /// </summary>
    public sealed class BenchmarkSession : IDisposable
    {
        private readonly string _sectionName;
        private readonly string _headingPrefix;
        private readonly List<BenchmarkEntry> _entries = new();

        internal BenchmarkSession(string sectionName, string headingPrefix)
        {
            _sectionName = sectionName;
            _headingPrefix = headingPrefix;

            Debug.Log("| Message Tech | Operations / Second | Allocations? |");
            Debug.Log("| ------------ | ------------------- | ------------ | ");
        }

        internal void Record(string messageTech, long operationsPerSecond, bool allocating)
        {
            string formattedOperations = operationsPerSecond.ToString(
                "N0",
                CultureInfo.InvariantCulture
            );
            Debug.Log($"| {messageTech} | {formattedOperations} | {(allocating ? "Yes" : "No")} |");
            _entries.Add(new BenchmarkEntry(messageTech, operationsPerSecond, allocating));
        }

        public void Dispose()
        {
            try
            {
                if (_entries.Count == 0)
                {
                    return;
                }

                Debug.Log(
                    BenchmarkDocumentation.BuildLogBlock(_sectionName, _headingPrefix, _entries)
                );
            }
            finally
            {
                _entries.Clear();
            }
        }
    }

    internal static class BenchmarkDocumentation
    {
        internal static string GetOperatingSystemSection()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "Windows";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "macOS";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "Linux";
            }

            return null;
        }

        internal static string BuildLogBlock(
            string sectionName,
            string headingPrefix,
            IReadOnlyList<BenchmarkEntry> entries
        )
        {
            StringBuilder builder = new();
            if (!string.IsNullOrEmpty(sectionName))
            {
                builder.Append(headingPrefix ?? string.Empty).AppendLine(sectionName);
                builder.AppendLine();
            }

            builder.AppendLine("| Message Tech | Operations / Second | Allocations? |");
            builder.AppendLine("| ------------ | ------------------- | ------------ |");

            foreach (BenchmarkEntry entry in entries)
            {
                builder
                    .Append("| ")
                    .Append(entry.MessageTech)
                    .Append(" | ")
                    .Append(entry.OperationsPerSecond.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" | ")
                    .Append(entry.Allocating ? "Yes" : "No")
                    .AppendLine(" |");
            }

            return builder.ToString().TrimEnd('\r', '\n');
        }
    }
}

#endif
