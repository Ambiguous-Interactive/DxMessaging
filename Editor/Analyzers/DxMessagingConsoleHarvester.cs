namespace DxMessaging.Editor.Analyzers
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using DxMessaging.Editor.Settings;
    using UnityEditor;
    using UnityEditor.Compilation;
    using UnityEngine;

    /// <summary>
    /// Per-type entry recorded by the inspector overlay's data feed.
    /// </summary>
    /// <remarks>
    /// This shape is the public contract that <c>MessageAwareComponentInspectorOverlay</c>
    /// consumes; the field names are kept short and lower-cased so Unity's
    /// <see cref="JsonUtility"/> serializer round-trips them cleanly through the JSON cache.
    /// </remarks>
    [Serializable]
    public sealed class BaseCallReportEntry
    {
        /// <summary>Fully-qualified name of the offending type.</summary>
        public string typeName;

        /// <summary>Method names whose overrides are missing the corresponding <c>base.*()</c> call.</summary>
        public List<string> missingBaseFor = new();

        /// <summary>Diagnostic IDs that contributed to this entry (e.g., DXMSG006, DXMSG007, DXMSG009, DXMSG010).</summary>
        /// <remarks>
        /// Note: the IL-reflection scanner classifies DXMSG009 as DXMSG007 because the two are
        /// indistinguishable at the IL level. The compile-time analyzer remains authoritative for
        /// the precise ID classification -- see the analyzer reference docs and the inspector
        /// integration section of <c>docs/reference/analyzers.md</c>. DXMSG008 (audit-marker for
        /// opted-out types) is intentionally NOT included here: opted-out types are excluded from
        /// the snapshot so the overlay's "Stop ignoring" path can reason about them via the
        /// project ignore list directly.
        /// </remarks>
        public List<string> diagnosticIds = new();

        /// <summary>Source file path (best-effort) for "Open Script" actions in the inspector overlay.</summary>
        public string filePath;

        /// <summary>1-based line number of the first relevant diagnostic, when known.</summary>
        public int line;
    }

    [Serializable]
    internal sealed class BaseCallReportFile
    {
        public int version = 1;
        public string generatedAt;
        public List<BaseCallReportEntry> types = new();
    }

    /// <summary>
    /// Builds the per-FQN snapshot consumed by the inspector overlay from a deterministic IL
    /// reflection scanner (<see cref="BaseCallTypeScanner"/>) and an optional compiler warning
    /// feed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Primary source (always-on): <see cref="BaseCallTypeScanner"/>.</b> Walks loaded
    /// <c>MessageAwareComponent</c> subclasses via Unity's <c>TypeCache</c> and inspects each
    /// override's IL body for the base-call shape. Deterministic across Unity 2021 cache hits,
    /// incremental compiles, and arbitrary domain-reload sequences -- the only inputs are the
    /// loaded assemblies in the AppDomain, which do not depend on Unity's compile-pipeline
    /// state. Runs on every <see cref="AssemblyReloadEvents.afterAssemblyReload"/> and on every
    /// <c>CompilationPipeline.assemblyCompilationFinished</c> burst (debounced via
    /// <see cref="EditorApplication.delayCall"/>).
    /// </para>
    /// <para>
    /// <b>Secondary source (opt-in): compiler warnings.</b>
    /// <see cref="DxMessagingSettings.UseConsoleBridge"/> adds diagnostics from the public
    /// <c>CompilationPipeline.assemblyCompilationFinished</c> event to the IL scanner's report.
    /// The event supplies messages from compilations observed in the current Editor session.
    /// </para>
    /// <para>
    /// The inspector overlay reads its snapshot from the unified per-FQN map populated here on
    /// every rescan. Use the menu <c>Tools > Wallstop Studios > DxMessaging > Rescan Base-Call Warnings</c> for a
    /// manual force-rescan.
    /// </para>
    /// <para>
    /// <see cref="IsAvailable"/> stays <c>true</c> as long as the static constructor itself does
    /// not throw -- the IL scanner is always wired, so the overlay never falls back to its
    /// degraded "harvester unavailable" warning surface in normal operation.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class DxMessagingConsoleHarvester
    {
        private const string ReportFileName = "baseCallReport.json";
        private const string ReportDirectoryName = "DxMessaging";
        private const double PollIntervalSeconds = 0.25;

        private static readonly Dictionary<string, BaseCallReportEntry> SnapshotInternal = new(StringComparer.Ordinal);

        // The Editor thread owns applied compiler reports and the merged snapshot.
        private static readonly BaseCallReportAggregator.CompilerReports _compilerReports = new();

        private static readonly HashSet<string> AlreadyWarned = new(StringComparer.Ordinal);

        /*
            Unity can report compiler messages from a worker thread. The lock protects the pending
            channel until the Editor thread takes ownership of the parsed reports.
        */
        private static readonly object _compilationFeedLock = new();

        /*
            Holds the latest CompilerMessage report for each assembly since the last idle drain.
        */
        private static readonly Dictionary<string, Dictionary<string, ParsedTypeReport>> _pendingByAssembly = new(StringComparer.OrdinalIgnoreCase);

        private static double _lastTickTime;
        private static int _lastSettingsRevision = -1;

        /*
            Latch flipped on by `OnAssemblyCompilationFinished` to coalesce the burst of one-event-
            per-assembly callbacks Unity fires during a build. We schedule a single deferred
            RescanNow via `EditorApplication.delayCall` (DrainScheduledRescan) and clear the latch
            when that callback runs. Without this debounce, a 30-assembly project would queue 30
            RescanNow invocations during the very window when the editor is most fragile.
        */
        private static volatile bool _rescanScheduled;

        /*
            Tracks whether the current snapshot has been refreshed by a scan in THIS Editor session,
            or whether it was loaded eagerly from `Library/DxMessaging/baseCallReport.json` in the
            static ctor and has not yet been overwritten. The inspector overlay reads this to
            distinguish "fresh-this-session" warnings from cached-from-previous-session warnings;
            when the cache is showing, we annotate the warning surface so the user
            understands the data may be stale until the first post-reload scan completes.

            Default `false`: the static ctor's `LoadFromDisk` runs first, so by the time anything
            observes the snapshot, either (a) the cache populated entries that pre-date this session,
            or (b) the cache was empty (truly fresh). In case (b) the overlay renders no warning
            anyway; there are no entries to annotate; so the false default is correct for both.
            Flipped to `true` after the first successful `RescanNow` post-startup; never flipped
            back to `false`. Volatile so the editor-loop reader sees the write without a memory
            barrier on Unity's pre-2022 mono runtime.
        */
        private static volatile bool _isFreshThisSession;

        /// <summary>
        /// Direct read of the latest console-derived report by FQN. Returns <c>true</c> if an
        /// entry exists for the given fully-qualified type name. The <paramref name="entry"/>
        /// reference points at the live snapshot row -- callers must not mutate it.
        /// </summary>
        /// <remarks>
        /// All mutation happens on the main thread inside <see cref="RescanNow"/>; the inspector
        /// overlay (also main thread) reads via this method one-call-per-frame-per-component, so
        /// there is no race that would justify the per-access defensive copy that
        /// <see cref="Snapshot"/> performs. Prefer this method in hot paths.
        /// </remarks>
        public static bool TryGetEntry(string fullyQualifiedTypeName, out BaseCallReportEntry entry)
        {
            if (string.IsNullOrEmpty(fullyQualifiedTypeName))
            {
                entry = null;
                return false;
            }
            return SnapshotInternal.TryGetValue(fullyQualifiedTypeName, out entry);
        }

        /// <summary>
        /// Read-only snapshot of the latest console-derived report, keyed by FQN.
        /// </summary>
        /// <remarks>
        /// Each access returns a fresh dictionary copy. Prefer <see cref="TryGetEntry"/> in hot
        /// paths (the inspector overlay) -- this property exists for callers that need to enumerate
        /// the full snapshot.
        /// </remarks>
        public static IReadOnlyDictionary<string, BaseCallReportEntry> Snapshot => new Dictionary<string, BaseCallReportEntry>(SnapshotInternal, StringComparer.Ordinal);

        /// <summary>
        /// <c>true</c> as long as the harvester has at least one functioning data source.
        /// </summary>
        /// <remarks>
        /// The <c>CompilationPipeline.assemblyCompilationFinished</c> feed is wired
        /// unconditionally on every supported Unity version, so this property is effectively
        /// always <c>true</c> in normal operation; it only flips to <c>false</c> when the static
        /// constructor itself throws (a hard initialization failure).
        /// The inspector overlay reads this property to decide whether to render its degraded
        /// warning surface, so the contract here is "should the overlay attempt to render at all".
        /// </remarks>
        public static bool IsAvailable { get; private set; } = true;

        /// <summary>Reports <c>false</c> for the retired Console history bridge.</summary>
        public static bool LogEntriesAvailable => false;

        /// <summary>
        /// <c>true</c> once the first <see cref="RescanNow"/> of this Editor session has produced
        /// a fresh snapshot; <c>false</c> while the inspector is still showing the on-disk cache
        /// loaded eagerly by the static constructor.
        /// </summary>
        /// <remarks>
        /// The inspector overlay reads this to annotate its warning surface: when <c>false</c> AND a
        /// warning is being shown, the overlay adds a "Report is cached from previous session;
        /// refreshing..." note so the user knows the data is from yesterday's scan and a fresh
        /// one is in flight. The flag is set inside <see cref="RescanNow"/> and never reset, so
        /// the note disappears as soon as the first post-reload scan lands and stays gone for
        /// the rest of the session.
        /// </remarks>
        public static bool IsFreshThisSession => _isFreshThisSession;

        /// <summary>Raised whenever the snapshot changes (after compilation, reload, or a settings change).</summary>
        public static event Action ReportUpdated;

        static DxMessagingConsoleHarvester()
        {
            try
            {
                LoadFromDisk();

                /*
                    AssetDatabase isn't fully ready inside the static ctor; defer the first scan
                    until Unity is idle so settings load doesn't fight a transitional asset-import
                    state.
                */
                ScheduleRescanWhenIdle();
                AssemblyReloadEvents.afterAssemblyReload += ScheduleRescanWhenIdle;
                CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
                EditorApplication.update += Tick;
            }
            catch (Exception ex)
            {
                DxMessagingEditorLog.LogWarning("DxMessagingConsoleHarvester failed to initialize.", ex);
                IsAvailable = false;
            }
        }

        /// <summary>
        /// Rescan loaded types and apply any pending compiler warnings.
        /// Called automatically on domain reload and compilation events; the menu entry exposes it
        /// for manual invocation. Settings setters (e.g.
        /// <see cref="DxMessagingSettings.BaseCallCheckEnabled"/>) call this via
        /// <see cref="EditorApplication.delayCall"/> so a re-enable repopulates the snapshot
        /// without waiting for the next settings check.
        /// </summary>
        [MenuItem("Tools/Wallstop Studios/DxMessaging/Rescan Base-Call Warnings")]
        public static void RescanNow()
        {
            if (!IsAvailable)
            {
                return;
            }

            /*
                Defer until compilation and asset imports finish. Settings reads stay passive and
                never create an asset.
            */
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            int settingsRevision = DxMessagingSettings.SettingsRevision;
            DxMessagingSettings settings = TryLoadSettings();
            _lastSettingsRevision = settingsRevision;
            if (settings != null && !settings._baseCallCheckEnabled)
            {
                bool wasNonEmpty = 0 < SnapshotInternal.Count;
                SnapshotInternal.Clear();
                _compilerReports.Clear();
                lock (_compilationFeedLock)
                {
                    _pendingByAssembly.Clear();
                }
                PersistToDisk();
                /*
                    The "check disabled" path still represents a successful session-time decision
                    about the snapshot; flip the freshness flag so the overlay never lingers in
                    "cached from previous session" mode after the user has explicitly silenced the
                    check. Doing this BEFORE RaiseReportUpdated mirrors the main path's ordering.
                */
                _isFreshThisSession = true;
                if (wasNonEmpty)
                {
                    RaiseReportUpdated();
                }
                return;
            }

            /*
                -- Primary source (always-on): IL-reflection scanner over loaded
                   MessageAwareComponent subclasses. Deterministic across Unity 2021 cache hits and
                   incremental compiles.
            */
            Dictionary<string, BaseCallReportEntry> scannerEntries;
            try
            {
                scannerEntries = BaseCallTypeScanner.Scan(settings);
            }
            catch (Exception ex)
            {
                LogExceptionOnce("scanner", "BaseCallTypeScanner.Scan threw.", ex);
                scannerEntries = new Dictionary<string, BaseCallReportEntry>(StringComparer.Ordinal);
            }

            /*
                The scanner produces a complete view of all loaded subclasses on every call, so it
                fully replaces the snapshot. Build the new map up-front from the scanner's output;
                we will add compiler warnings below if the user opted in.
            */
            Dictionary<string, BaseCallReportEntry> nextSnapshot = new(scannerEntries, StringComparer.Ordinal);

            bool useBridge = settings != null && settings._useConsoleBridge;

            if (useBridge)
            {
                Dictionary<string, Dictionary<string, ParsedTypeReport>> drained;
                lock (_compilationFeedLock)
                {
                    if (_pendingByAssembly.Count == 0)
                    {
                        drained = null;
                    }
                    else
                    {
                        drained = new Dictionary<string, Dictionary<string, ParsedTypeReport>>(_pendingByAssembly, StringComparer.OrdinalIgnoreCase);
                        _pendingByAssembly.Clear();
                    }
                }

                ApplyCompilerMessageDrain(drained);

                /*
                    The compiler feed can contribute methods or diagnostic IDs the scanner missed.
                    It cannot override the scanner's classification.
                */
                try
                {
                    Dictionary<string, BaseCallReportEntryDto> bridgeSnapshot = _compilerReports.BuildMergedSnapshot(null);
                    UnionBridgeIntoSnapshot(bridgeSnapshot, nextSnapshot);
                }
                catch (Exception ex)
                {
                    LogExceptionOnce("aggregate", "Snapshot merge failed.", ex);
                    /*
                        Keep the scanner snapshot if merging compiler warnings fails.
                    */
                }
            }
            else
            {
                /*
                    Discard compiler messages when the bridge is disabled so stale warnings do
                    not appear when the user enables it later.
                */
                lock (_compilationFeedLock)
                {
                    _pendingByAssembly.Clear();
                }
                _compilerReports.Clear();
            }

            /*
                Replace the live snapshot with the new view in one swap. The scanner runs over ALL
                loaded types every time, so this is a full-replace; types the user has fixed since
                the last scan disappear, types newly broken appear.
            */
            SnapshotInternal.Clear();
            foreach (KeyValuePair<string, BaseCallReportEntry> kvp in nextSnapshot)
            {
                SnapshotInternal[kvp.Key] = kvp.Value;
            }

            PersistToDisk();
            /*
                Mark the snapshot as session-fresh AFTER the persist + before the event fires, so
                that any subscriber repainting the inspector observes the same "fresh" state the
                overlay will see on its next read. Subsequent scans are no-ops on this flag.
            */
            _isFreshThisSession = true;
            RaiseReportUpdated();
        }

        /*
            Unions the bridge-produced DTOs into the scanner-produced snapshot. The scanner is the
            authoritative source; the bridge can only contribute methods / diagnostic ids the
            scanner missed for a type, OR a brand-new type entry the scanner did not produce (e.g.
            a subclass the scanner couldn't classify because its IL was stripped). The first non-
            empty file path / line wins, matching the bridge's pre-existing semantics.
        */
        private static void UnionBridgeIntoSnapshot(Dictionary<string, BaseCallReportEntryDto> bridgeSnapshot, Dictionary<string, BaseCallReportEntry> scannerSnapshot)
        {
            if (bridgeSnapshot is null || bridgeSnapshot.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<string, BaseCallReportEntryDto> kvp in bridgeSnapshot)
            {
                BaseCallReportEntryDto dto = kvp.Value;
                if (dto is null || string.IsNullOrEmpty(dto.TypeName))
                {
                    continue;
                }
                if (!scannerSnapshot.TryGetValue(dto.TypeName, out BaseCallReportEntry existing))
                {
                    existing = new BaseCallReportEntry
                    {
                        typeName = dto.TypeName,
                        missingBaseFor = new List<string>(dto.MissingBaseFor),
                        diagnosticIds = dto.DiagnosticIds.ToList(),
                        filePath = dto.FilePath ?? string.Empty,
                        line = dto.Line,
                    };
                    scannerSnapshot[dto.TypeName] = existing;
                    continue;
                }
                foreach (string method in dto.MissingBaseFor)
                {
                    if (!string.IsNullOrEmpty(method) && !existing.missingBaseFor.Contains(method, StringComparer.Ordinal))
                    {
                        existing.missingBaseFor.Add(method);
                    }
                }
                foreach (string id in dto.DiagnosticIds)
                {
                    if (!string.IsNullOrEmpty(id) && !existing.diagnosticIds.Contains(id, StringComparer.Ordinal))
                    {
                        existing.diagnosticIds.Add(id);
                    }
                }
                if (string.IsNullOrEmpty(existing.filePath) && !string.IsNullOrEmpty(dto.FilePath))
                {
                    existing.filePath = dto.FilePath;
                    existing.line = dto.Line;
                }
            }
        }

        private static void ApplyCompilerMessageDrain(Dictionary<string, Dictionary<string, ParsedTypeReport>> drained)
        {
            if (drained is null || drained.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, Dictionary<string, ParsedTypeReport>> kvp in drained)
            {
                _compilerReports.Replace(kvp.Key, kvp.Value);
            }
        }

        /// <summary>Schedules a rescan after compilation and asset imports finish.</summary>
        public static void RequestRescan()
        {
            ScheduleRescanWhenIdle();
        }

        private static void Tick()
        {
            if (!IsAvailable || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            try
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastTickTime < PollIntervalSeconds)
                {
                    return;
                }
                _lastTickTime = now;

                if (_lastSettingsRevision != DxMessagingSettings.SettingsRevision)
                {
                    RescanNow();
                }
            }
            catch (Exception ex)
            {
                LogExceptionOnce("tick", "Tick failed.", ex);
            }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            /*
                Each assembly supplies its own messages. Parse those messages here and defer
                the settings check and report update until Unity is idle.
            */
            try
            {
                if (!string.IsNullOrEmpty(assemblyPath) && messages != null)
                {
                    Dictionary<string, ParsedTypeReport> aggregated = new(StringComparer.Ordinal);
                    foreach (CompilerMessage compilerMessage in messages)
                    {
                        string body = compilerMessage.message;
                        if (!BaseCallLogMessageParser.IsDiagnosticCandidate(body) || BaseCallLogMessageParser.ParseLine(body) is not ParsedEntry entry)
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(entry.FilePath) && !string.IsNullOrEmpty(compilerMessage.file))
                        {
                            entry = new ParsedEntry(entry.DiagnosticId, entry.TypeFullName, entry.MethodName, compilerMessage.file, Math.Max(0, compilerMessage.line));
                        }

                        BaseCallLogMessageParser.AddEntry(aggregated, entry);
                    }

                    lock (_compilationFeedLock)
                    {
                        /*
                            Even when this assembly produced zero matching messages, we still want
                            an empty entry so DrainScheduledRescan can RETIRE the assembly's prior
                            attribution (the user fixed every offending type in this assembly).
                        */
                        _pendingByAssembly[assemblyPath] = aggregated;
                    }
                }
            }
            catch (Exception ex)
            {
                LogExceptionOnce("compilation-parse", $"Failed to parse CompilerMessage payload for {assemblyPath}.", ex);
            }

            /*
                Schedule a single delayed drain. The callback rechecks editor state and requeues if
                Unity is still compiling/updating, so this never drops the final rescan.
            */
            ScheduleRescanWhenIdle();
        }

        internal static void ScheduleRescanWhenIdle()
        {
            if (!IsAvailable || _rescanScheduled)
            {
                return;
            }
            _rescanScheduled = true;
            EditorApplication.delayCall += DrainScheduledRescan;
        }

        private static void DrainScheduledRescan()
        {
            _rescanScheduled = false;
            /*
                delayCall can fire while still mid-compile if the editor is in a weird state.
                RescanNow has its own isCompiling/isUpdating guard; re-defer if needed.
            */
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (!_rescanScheduled)
                {
                    _rescanScheduled = true;
                    EditorApplication.delayCall += DrainScheduledRescan;
                }
                return;
            }
            SafeRescanFromCallback();
        }

        private static void SafeRescanFromCallback()
        {
            try
            {
                RescanNow();
            }
            catch (Exception ex)
            {
                LogExceptionOnce("rescan-callback", "RescanNow callback threw.", ex);
            }
        }

        private static DxMessagingSettings TryLoadSettings()
        {
            /*
                CRITICAL: passive load only. We must NOT call GetOrCreateSettings here; that path
                can call AssetDatabase.CreateAsset, which during script compilation schedules an
                import → re-triggers compilation → permanent freeze. The Project Settings page and
                the inspector overlay both call GetOrCreateSettings on demand (outside compilation),
                so the asset is materialised through normal user interaction. If the asset doesn't
                exist yet (fresh project, first compile), the harvester treats the snapshot as
                unconfigured and behaves as if the master toggle is enabled (default behaviour).
            */
            try
            {
                return DxMessagingSettings.LoadSettingsPassive();
            }
            catch (Exception ex)
            {
                LogExceptionOnce("settings", "Could not load DxMessagingSettings.", ex);
                return null;
            }
        }

        private static void LogExceptionOnce(string key, string message, Exception exception)
        {
            if (!AlreadyWarned.Add(key))
            {
                return;
            }
            DxMessagingEditorLog.LogWarning(message, exception);
        }

        private static void RaiseReportUpdated()
        {
            try
            {
                ReportUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                LogExceptionOnce("report-updated", "ReportUpdated subscriber threw.", ex);
            }
        }

        /*
            -- JSON persistence -----------------------------------------------------------------
            The cache survives editor restarts so the overlay has data to render before the first
            post-launch rescan completes; it is rewritten on every successful rescan.
        */

        internal static void PersistToDisk()
        {
            try
            {
                string absolutePath = GetReportFilePath();
                EnsureDirectoryExists(absolutePath);

                BaseCallReportFile file = new()
                {
                    version = 1,
                    generatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    types = SnapshotInternal.Values.OrderBy(e => e.typeName, StringComparer.Ordinal).ToList(),
                };

                string json = JsonUtility.ToJson(file, prettyPrint: true);
                File.WriteAllText(absolutePath, json);
            }
            catch (Exception ex)
            {
                LogExceptionOnce("persist", "Failed to persist analyzer diagnostics report.", ex);
            }
        }

        internal static void LoadFromDisk()
        {
            try
            {
                string absolutePath = GetReportFilePath();
                if (!File.Exists(absolutePath))
                {
                    return;
                }

                string json = File.ReadAllText(absolutePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                BaseCallReportFile file = JsonUtility.FromJson<BaseCallReportFile>(json);
                if (file?.types == null)
                {
                    return;
                }

                SnapshotInternal.Clear();
                foreach (BaseCallReportEntry entry in file.types)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.typeName))
                    {
                        continue;
                    }
                    entry.missingBaseFor ??= new List<string>();
                    entry.diagnosticIds ??= new List<string>();
                    SnapshotInternal[entry.typeName] = entry;
                }
            }
            catch (Exception ex)
            {
                LogExceptionOnce("load", "Failed to load analyzer diagnostics report.", ex);
            }
        }

        internal static string GetReportFilePath()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace("\\", "/");
            return Path.Combine(projectRoot, "Library", ReportDirectoryName, ReportFileName).Replace("\\", "/");
        }

        private static void EnsureDirectoryExists(string absolutePath)
        {
            string directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
#endif
}
