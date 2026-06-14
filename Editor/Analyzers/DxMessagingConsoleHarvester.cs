namespace DxMessaging.Editor.Analyzers
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
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
    /// reflection scanner (<see cref="BaseCallTypeScanner"/>) -- and, optionally, a legacy
    /// console-scrape bridge for users who want the union of both data sources.
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
    /// <b>Secondary source (opt-in): legacy console-scrape bridge.</b> When
    /// <see cref="DxMessagingSettings.UseConsoleBridge"/> is <c>true</c>, the harvester ALSO
    /// reads warnings from <c>UnityEditor.LogEntries</c> via reflection and from
    /// <c>CompilationPipeline.assemblyCompilationFinished</c>'s per-assembly
    /// <c>CompilerMessage[]</c> payloads. This path is non-deterministic on Unity 2021 (Bee/csc
    /// cache hits cause Unity to skip surfacing analyzer warnings to either store) and is the
    /// reason the IL-reflection scanner exists. Default off; available for users who want the
    /// union of both data sources.
    /// </para>
    /// <para>
    /// The inspector overlay reads its snapshot from the unified per-FQN map populated here on
    /// every rescan. Use the menu <c>Tools > DxMessaging > Rescan Base-Call Warnings</c> for a
    /// manual force-rescan.
    /// </para>
    /// <para>
    /// <see cref="IsAvailable"/> stays <c>true</c> as long as the static constructor itself does
    /// not throw -- the IL scanner is always wired, so the overlay never falls back to its
    /// degraded "harvester unavailable" HelpBox in normal operation. <see cref="LogEntriesAvailable"/>
    /// continues to report whether the legacy reflection layer is bindable, for diagnostics only.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class DxMessagingConsoleHarvester
    {
        private const string ReportFileName = "baseCallReport.json";
        private const string ReportDirectoryName = "DxMessaging";
        private const double PollIntervalSeconds = 0.25;

        // N1: cap the per-rescan list capacity so a 100k-warning console doesn't allocate a
        // pathologically large initial backing array. The list still grows freely if the console
        // really does hold more entries than this, but the OOM-edge becomes a non-issue.
        private const int MaxLineListInitialCapacity = 1024;

        private static readonly Dictionary<string, BaseCallReportEntry> SnapshotInternal = new(
            StringComparer.Ordinal
        );

        // Per-assembly attribution for the LEGACY CompilationPipeline.assemblyCompilationFinished
        // feed (only consulted when DxMessagingSettings.UseConsoleBridge is true). When a
        // recompile no longer reports a previously-seen type (because the user fixed the missing
        // base call), we drop it from the bridge merged view. Without per-assembly tracking we'd
        // never know which entries to retire.
        //
        // Lifecycle: writes happen inside _compilationFeedLock from
        // OnAssemblyCompilationFinished. Reads happen on the editor main thread inside RescanNow,
        // also under the lock to flush + clear the channel atomically.
        //
        // The merge + retirement bookkeeping for these maps lives in
        // <see cref="BaseCallReportAggregator"/> as a pure helper so it can be tested via
        // dotnet-test (the harvester itself is Unity-only and cannot be loaded outside the
        // editor). Mutations to _typesByAssembly and _compilationMerged go through that helper
        // exclusively to keep the test surface and runtime behaviour identical.
        //
        // Note: starting in v2.3, the IL-reflection scanner (BaseCallTypeScanner) is the primary
        // source of truth; it runs unconditionally on every rescan, regardless of bridge state.
        // The bridge only contributes ADDITIONAL data, never overrides the scanner.
        private static readonly Dictionary<string, HashSet<string>> _typesByAssembly = new(
            StringComparer.OrdinalIgnoreCase
        );

        // Per-FQN merged view of every assembly's latest reports, kept in sync with
        // _typesByAssembly by BaseCallReportAggregator.ApplyAssemblyReports. The final inspector
        // snapshot is built by unioning this with the LogEntries-derived report.
        private static readonly Dictionary<string, ParsedTypeReport> _compilationMerged = new(
            StringComparer.Ordinal
        );

        private static readonly HashSet<string> AlreadyWarned = new(StringComparer.Ordinal);

        // Lock guarding all reads/writes to _typesByAssembly and the parsed-message buffer that
        // flows from OnAssemblyCompilationFinished (worker thread for the parse) into
        // DrainScheduledRescan (editor main thread for snapshot integration). Unity can fire
        // assemblyCompilationFinished from a non-main thread on some Editor versions; the rest
        // of the harvester (LogEntries reflection, AssetDatabase, persistence) is main-thread
        // only and uses simple read order, so the lock is scoped to the cross-thread channel.
        private static readonly object _compilationFeedLock = new();

        // Drained by DrainScheduledRescan on the next editor tick. Holds the union of all
        // CompilerMessage payloads captured since the last drain, attributed to their source
        // assembly so we can retire entries that the user has fixed.
        private static readonly Dictionary<
            string,
            Dictionary<string, ParsedTypeReport>
        > _pendingByAssembly = new(StringComparer.OrdinalIgnoreCase);

        // True when the LogEntries reflection layer failed to bind. The harvester remains
        // available via the CompilerMessage path; this flag just gates the LogEntries-specific
        // code paths (Tick polling, RescanNow's reflection call). Renamed from `_disabled` so
        // the name reflects what it actually means.
        private static readonly bool _logEntriesDisabled;

        // Reflection handles. Resolved once in the static ctor; null when the running Unity version
        // does not expose the expected LogEntries shape.
        private static readonly Type _logEntryType;
        private static readonly MethodInfo _startGettingEntries;
        private static readonly MethodInfo _endGettingEntries;
        private static readonly MethodInfo _getEntryInternal;
        private static readonly MethodInfo _getCount;
        private static readonly FieldInfo _messageField;

        private static double _lastTickTime;
        private static int _lastSeenCount;

        // Latch flipped on by `OnAssemblyCompilationFinished` to coalesce the burst of one-event-
        // per-assembly callbacks Unity fires during a build. We schedule a single deferred
        // RescanNow via `EditorApplication.delayCall` (DrainScheduledRescan) and clear the latch
        // when that callback runs. Without this debounce, a 30-assembly project would queue 30
        // RescanNow invocations during the very window when the editor is most fragile.
        private static volatile bool _rescanScheduled;

        // Tracks whether the current snapshot has been refreshed by a scan in THIS Editor session,
        // or whether it was loaded eagerly from `Library/DxMessaging/baseCallReport.json` in the
        // static ctor and has not yet been overwritten. The inspector overlay reads this to
        // distinguish "fresh-this-session" warnings from cached-from-previous-session warnings;
        // when the cache is showing, we annotate the HelpBox with a small suffix so the user
        // understands the data may be stale until the first post-reload scan completes.
        //
        // Default `false`: the static ctor's `LoadFromDisk` runs first, so by the time anything
        // observes the snapshot, either (a) the cache populated entries that pre-date this session,
        // or (b) the cache was empty (truly fresh). In case (b) the overlay renders no warning
        // anyway; there are no entries to annotate; so the false default is correct for both.
        // Flipped to `true` after the first successful `RescanNow` post-startup; never flipped
        // back to `false`. Volatile so the editor-loop reader sees the write without a memory
        // barrier on Unity's pre-2022 mono runtime.
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
        public static IReadOnlyDictionary<string, BaseCallReportEntry> Snapshot =>
            new Dictionary<string, BaseCallReportEntry>(SnapshotInternal, StringComparer.Ordinal);

        /// <summary>
        /// <c>true</c> as long as the harvester has at least one functioning data source.
        /// </summary>
        /// <remarks>
        /// The <c>CompilationPipeline.assemblyCompilationFinished</c> feed is wired
        /// unconditionally on every supported Unity version, so this property is effectively
        /// always <c>true</c> in normal operation; it only flips to <c>false</c> when the static
        /// constructor itself throws (a hard initialization failure). The LogEntries reflection
        /// layer is the optional source -- see <see cref="LogEntriesAvailable"/> for that flag.
        /// The inspector overlay reads this property to decide whether to render its degraded
        /// HelpBox, so the contract here is "should the overlay attempt to render at all".
        /// </remarks>
        public static bool IsAvailable { get; private set; } = true;

        /// <summary>
        /// <c>true</c> when the legacy <c>UnityEditor.LogEntries</c> reflection layer resolved
        /// successfully on this Unity version. The harvester does not require this to be true to
        /// function -- Unity 2021's analyzer warnings flow through the CompilerMessage feed
        /// instead. Exposed primarily for diagnostics / tests.
        /// </summary>
        public static bool LogEntriesAvailable => !_logEntriesDisabled;

        /// <summary>
        /// <c>true</c> once the first <see cref="RescanNow"/> of this Editor session has produced
        /// a fresh snapshot; <c>false</c> while the inspector is still showing the on-disk cache
        /// loaded eagerly by the static constructor.
        /// </summary>
        /// <remarks>
        /// The inspector overlay reads this to annotate its HelpBox: when <c>false</c> AND a
        /// warning is being shown, the overlay appends a "(cached from previous session --
        /// refreshing...)" suffix so the user knows the data is from yesterday's scan and a fresh
        /// one is in flight. The flag is set inside <see cref="RescanNow"/> and never reset, so
        /// the suffix disappears as soon as the first post-reload scan lands and stays gone for
        /// the rest of the session.
        /// </remarks>
        public static bool IsFreshThisSession => _isFreshThisSession;

        /// <summary>Raised whenever the snapshot changes (post-compile, post-domain-reload, or polled console-count change).</summary>
        public static event Action ReportUpdated;

        static DxMessagingConsoleHarvester()
        {
            try
            {
                Type logEntriesType =
                    Type.GetType("UnityEditor.LogEntries,UnityEditor.dll")
                    // S9: legacy / future-Unity probe. UnityEditorInternal.LogEntries doesn't
                    // exist today, but documenting the fallback as a one-liner keeps us forward-
                    // compatible at zero cost.
                    ?? Type.GetType("UnityEditorInternal.LogEntries,UnityEditor.dll");
                _logEntryType =
                    Type.GetType("UnityEditor.LogEntry,UnityEditor.dll")
                    ?? Type.GetType("UnityEditorInternal.LogEntry,UnityEditor.dll");

                bool logEntriesBound = false;
                if (logEntriesType is not null && _logEntryType is not null)
                {
                    if (_logEntryType.IsValueType)
                    {
                        // S8: defensive value-type guard. If a future Unity version makes LogEntry
                        // a struct, Activator.CreateInstance would hand us a boxed copy and the
                        // GetEntry call would mutate that copy in-place; harvest would silently
                        // report empty. Disable the LogEntries path rather than silently producing
                        // a wrong result; the CompilerMessage feed still runs.
                        LogOnce(
                            "logentry-is-struct",
                            "LogEntry is a value type on this Unity version; LogEntries scanning disabled. Falling back to the CompilerMessage feed."
                        );
                    }
                    else
                    {
                        _startGettingEntries = SafeGetStaticMethod(
                            logEntriesType,
                            "StartGettingEntries"
                        );
                        _endGettingEntries = SafeGetStaticMethod(
                            logEntriesType,
                            "EndGettingEntries"
                        );
                        _getEntryInternal = SafeGetStaticMethod(logEntriesType, "GetEntryInternal");
                        _getCount = SafeGetStaticMethod(logEntriesType, "GetCount");
                        _messageField = SafeGetInstanceField(_logEntryType, "message");

                        logEntriesBound =
                            _startGettingEntries is not null
                            && _endGettingEntries is not null
                            && _getEntryInternal is not null
                            && _getCount is not null
                            && _messageField is not null;
                    }
                }

                if (!logEntriesBound)
                {
                    // The LogEntries reflection layer is unavailable. The IL-reflection scanner
                    // is the primary data source so this is no longer a critical path; the
                    // log-once is kept for diagnostic purposes (and only matters when the user
                    // has enabled the legacy bridge via DxMessagingSettings.UseConsoleBridge).
                    LogOnce(
                        "reflection-fallback",
                        "LogEntries reflection unavailable on this Unity version. The IL-reflection "
                            + "scanner remains the primary data source; the legacy console-scrape bridge "
                            + "(opt-in via DxMessagingSettings.UseConsoleBridge) cannot read LogEntries on this version."
                    );
                    _logEntriesDisabled = true;
                }

                LoadFromDisk();

                // AssetDatabase isn't fully ready inside the static ctor; defer the first scan
                // until Unity is idle so settings load doesn't fight a transitional asset-import
                // state.
                ScheduleRescanWhenIdle();
                AssemblyReloadEvents.afterAssemblyReload += ScheduleRescanWhenIdle;
                CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
                if (!_logEntriesDisabled)
                {
                    EditorApplication.update += Tick;
                }
            }
            catch (Exception ex)
            {
                DxMessagingEditorLog.LogWarning(
                    "DxMessagingConsoleHarvester failed to initialize.",
                    ex
                );
                _logEntriesDisabled = true;
                IsAvailable = false;
            }
        }

        /// <summary>
        /// Force a re-read of the editor console and drain any pending CompilerMessage payloads.
        /// Called automatically on domain reload and compilation events; the menu entry exposes it
        /// for manual invocation. Settings setters (e.g.
        /// <see cref="DxMessagingSettings.BaseCallCheckEnabled"/>) call this via
        /// <see cref="EditorApplication.delayCall"/> so a re-enable repopulates the snapshot
        /// without waiting for the next polled tick.
        /// </summary>
        [MenuItem("Tools/Wallstop Studios/DxMessaging/Rescan Base-Call Warnings")]
        public static void RescanNow()
        {
            if (!IsAvailable)
            {
                return;
            }

            // Critical: NEVER touch LogEntries reflection or AssetDatabase while Unity is mid-
            // compile or mid-asset-update. Reading LogEntries during compilation contends with the
            // compiler's own log-buffer lock and can deadlock the editor. Touching AssetDatabase
            // (via TryLoadSettings → GetOrCreateSettings → CreateAsset) during compilation
            // schedules an import that re-triggers compilation; an infinite-loop trap that
            // permanently freezes script-compilation startup. Defer to the post-compile state
            // and let the polled tick (or the explicit afterAssemblyReload hook) pick it up.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            DxMessagingSettings settings = TryLoadSettings();
            if (settings != null && !settings._baseCallCheckEnabled)
            {
                bool wasNonEmpty = SnapshotInternal.Count > 0;
                SnapshotInternal.Clear();
                // S3: keep the per-assembly bookkeeping (_typesByAssembly + _compilationMerged)
                // in lock-step. Clearing only one half leaves stale rows that the next
                // ApplyAssemblyReports call would silently re-promote into the snapshot when the
                // user toggles the master switch back on without an intervening recompile.
                _typesByAssembly.Clear();
                _compilationMerged.Clear();
                lock (_compilationFeedLock)
                {
                    _pendingByAssembly.Clear();
                }
                _lastSeenCount = 0;
                PersistToDisk();
                // The "check disabled" path still represents a successful session-time decision
                // about the snapshot; flip the freshness flag so the overlay never lingers in
                // "cached from previous session" mode after the user has explicitly silenced the
                // check. Doing this BEFORE RaiseReportUpdated mirrors the main path's ordering.
                _isFreshThisSession = true;
                if (wasNonEmpty)
                {
                    RaiseReportUpdated();
                }
                return;
            }

            // -- Primary source (always-on): IL-reflection scanner over loaded
            //    MessageAwareComponent subclasses. Deterministic across Unity 2021 cache hits and
            //    incremental compiles; replaces the lossy console-scrape harvester as the
            //    inspector overlay's source of truth.
            Dictionary<string, BaseCallReportEntry> scannerEntries;
            try
            {
                scannerEntries = BaseCallTypeScanner.Scan(settings);
            }
            catch (Exception ex)
            {
                LogExceptionOnce("scanner", "BaseCallTypeScanner.Scan threw.", ex);
                scannerEntries = new Dictionary<string, BaseCallReportEntry>(
                    StringComparer.Ordinal
                );
            }

            // The scanner produces a complete view of all loaded subclasses on every call, so it
            // fully replaces the snapshot. Build the new map up-front from the scanner's output;
            // we'll union the legacy-bridge entries into it below if the user opted in.
            Dictionary<string, BaseCallReportEntry> nextSnapshot = new(
                scannerEntries,
                StringComparer.Ordinal
            );

            bool useBridge = settings != null && settings._useConsoleBridge;

            int currentCount = 0;
            bool logEntriesHarvested = false;
            if (useBridge)
            {
                // -- Secondary source (opt-in): LogEntries reflection (Unity 2022+ reliable path).
                Dictionary<string, ParsedTypeReport> logEntriesAggregate = HarvestFromLogEntries(
                    out currentCount,
                    out logEntriesHarvested
                );

                // -- Secondary source (opt-in): pending CompilerMessage payloads (Unity 2021's
                //    primary path under the legacy bridge). Drain the cross-thread channel
                //    atomically.
                Dictionary<string, Dictionary<string, ParsedTypeReport>> drained;
                lock (_compilationFeedLock)
                {
                    if (_pendingByAssembly.Count == 0)
                    {
                        drained = null;
                    }
                    else
                    {
                        drained = new Dictionary<string, Dictionary<string, ParsedTypeReport>>(
                            _pendingByAssembly,
                            StringComparer.OrdinalIgnoreCase
                        );
                        _pendingByAssembly.Clear();
                    }
                }

                ApplyCompilerMessageDrain(drained);

                // Merge the bridge view (LogEntries + CompilerMessage) and union it INTO the
                // scanner-produced snapshot. The scanner is authoritative; the bridge can only
                // ADD methods/diagnostic ids it sees that the scanner missed (e.g. exotic IL
                // shapes the byte walker stepped past). Bridge entries never override the
                // scanner's classification.
                try
                {
                    Dictionary<string, BaseCallReportEntryDto> bridgeSnapshot =
                        BaseCallReportAggregator.BuildSnapshot(
                            logEntriesHarvested ? logEntriesAggregate : null,
                            _compilationMerged
                        );
                    UnionBridgeIntoSnapshot(bridgeSnapshot, nextSnapshot);
                }
                catch (Exception ex)
                {
                    LogExceptionOnce("aggregate", "Snapshot merge failed.", ex);
                    // Fall through with the scanner-only snapshot; partial data is better than
                    // wiping the snapshot when the bridge half misbehaves.
                }
            }
            else
            {
                // Bridge is disabled: drop any pending CompilerMessage entries the harvester may
                // have buffered (they would otherwise leak into the snapshot the next time the
                // user toggles the bridge on). The bridge bookkeeping is reset below as well.
                lock (_compilationFeedLock)
                {
                    _pendingByAssembly.Clear();
                }
                _typesByAssembly.Clear();
                _compilationMerged.Clear();
            }

            // Replace the live snapshot with the new view in one swap. The scanner runs over ALL
            // loaded types every time, so this is a full-replace; types the user has fixed since
            // the last scan disappear, types newly broken appear.
            SnapshotInternal.Clear();
            foreach (KeyValuePair<string, BaseCallReportEntry> kvp in nextSnapshot)
            {
                SnapshotInternal[kvp.Key] = kvp.Value;
            }

            if (useBridge && logEntriesHarvested)
            {
                _lastSeenCount = currentCount;
            }
            PersistToDisk();
            // Mark the snapshot as session-fresh AFTER the persist + before the event fires, so
            // that any subscriber repainting the inspector observes the same "fresh" state the
            // overlay will see on its next read. Subsequent scans are no-ops on this flag.
            _isFreshThisSession = true;
            RaiseReportUpdated();
        }

        // Unions the bridge-produced DTOs into the scanner-produced snapshot. The scanner is the
        // authoritative source; the bridge can only contribute methods / diagnostic ids the
        // scanner missed for a type, OR a brand-new type entry the scanner did not produce (e.g.
        // a subclass the scanner couldn't classify because its IL was stripped). The first non-
        // empty file path / line wins, matching the bridge's pre-existing semantics.
        private static void UnionBridgeIntoSnapshot(
            Dictionary<string, BaseCallReportEntryDto> bridgeSnapshot,
            Dictionary<string, BaseCallReportEntry> scannerSnapshot
        )
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
                    if (
                        !string.IsNullOrEmpty(method)
                        && !existing.missingBaseFor.Contains(method, StringComparer.Ordinal)
                    )
                    {
                        existing.missingBaseFor.Add(method);
                    }
                }
                foreach (string id in dto.DiagnosticIds)
                {
                    if (
                        !string.IsNullOrEmpty(id)
                        && !existing.diagnosticIds.Contains(id, StringComparer.Ordinal)
                    )
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

        // Reads the editor console via LogEntries reflection. Returns the aggregated per-type
        // report, the current console count, and whether the harvest actually ran (false when
        // the LogEntries reflection layer is unavailable or threw). On Unity 2021 this returns
        // an empty aggregate every time; the analyzer warnings flow through the CompilerMessage
        // feed instead and arrive via ApplyCompilerMessageDrain.
        private static Dictionary<string, ParsedTypeReport> HarvestFromLogEntries(
            out int currentCount,
            out bool harvested
        )
        {
            currentCount = 0;
            harvested = false;
            if (_logEntriesDisabled)
            {
                return new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal);
            }

            try
            {
                currentCount = (int)_getCount.Invoke(null, null);
            }
            catch (Exception ex)
            {
                LogExceptionOnce("getcount", "GetCount invocation failed.", ex);
                return new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal);
            }

            // S4: console-clear handling. We always overwrite _lastSeenCount near the bottom of
            // RescanNow, so the only point of acting on a shrunken count here is to be explicit
            // about the semantic. The accumulator is rebuilt from scratch every rescan, so the
            // clear case is naturally consistent; even an empty log produces an empty aggregate
            // and a ReportUpdated fire that drops stale rows.

            // B2 + S6: enter the get/end pair only AFTER StartGettingEntries actually succeeded.
            try
            {
                _startGettingEntries.Invoke(null, null);
            }
            catch (Exception ex)
            {
                LogExceptionOnce("start", "StartGettingEntries invocation failed.", ex);
                return new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal);
            }

            // N1: clamp the initial capacity to a sane ceiling. The list is allowed to grow past
            // this if the console really does hold more entries; we just don't blow up the heap on
            // first allocation.
            List<string> lines = new(Math.Min(currentCount, MaxLineListInitialCapacity));
            int harvestedCount = currentCount;
            try
            {
                if (_startGettingEntries.ReturnType == typeof(int))
                {
                    // The Invoke return value is intentionally discarded; we re-pull via GetCount
                    // because the polled count is authoritative.
                    try
                    {
                        harvestedCount = (int)_getCount.Invoke(null, null);
                    }
                    catch (Exception ex)
                    {
                        LogExceptionOnce(
                            "getcount-after-start",
                            "GetCount after StartGettingEntries failed.",
                            ex
                        );
                        // Retain the previous count.
                    }
                }

                object entryInstance = Activator.CreateInstance(_logEntryType);
                object[] invokeArgs = new object[2];
                invokeArgs[1] = entryInstance;
                for (int j = 0; j < harvestedCount; j++)
                {
                    invokeArgs[0] = j;
                    try
                    {
                        _getEntryInternal.Invoke(null, invokeArgs);
                    }
                    catch (Exception ex)
                    {
                        LogExceptionOnce(
                            "getentry",
                            $"GetEntryInternal invocation failed at index {j}.",
                            ex
                        );
                        continue;
                    }

                    string message;
                    try
                    {
                        message = _messageField.GetValue(entryInstance) as string;
                    }
                    catch (Exception ex)
                    {
                        LogExceptionOnce(
                            "getmessage",
                            $"LogEntry.message read failed at index {j}.",
                            ex
                        );
                        continue;
                    }

                    if (!string.IsNullOrEmpty(message))
                    {
                        lines.Add(message);
                    }
                }
            }
            catch (Exception ex)
            {
                LogExceptionOnce("harvest", "Harvest loop failed.", ex);
            }
            finally
            {
                try
                {
                    _endGettingEntries.Invoke(null, null);
                }
                catch (Exception ex)
                {
                    LogExceptionOnce("end", "EndGettingEntries invocation failed.", ex);
                }
            }

            harvested = true;
            try
            {
                return BaseCallLogMessageParser.Aggregate(lines);
            }
            catch (Exception ex)
            {
                LogExceptionOnce(
                    "aggregate-logentries",
                    "Aggregating LogEntries lines failed.",
                    ex
                );
                return new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal);
            }
        }

        // Folds a freshly-drained per-assembly batch into the long-lived per-assembly bookkeeping
        // via <see cref="BaseCallReportAggregator.ApplyAssemblyReports"/>. The aggregator owns the
        // retirement logic (a type the user fixed disappears as soon as the assembly recompiles
        // without re-reporting it) and the cross-assembly survival rule (a type stays in the
        // merged view as long as ANY assembly still reports it).
        private static void ApplyCompilerMessageDrain(
            Dictionary<string, Dictionary<string, ParsedTypeReport>> drained
        )
        {
            if (drained is null || drained.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, Dictionary<string, ParsedTypeReport>> kvp in drained)
            {
                BaseCallReportAggregator.ApplyAssemblyReports(
                    kvp.Key,
                    kvp.Value ?? new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal),
                    _typesByAssembly,
                    _compilationMerged
                );
            }
        }

        /// <summary>
        /// Hint the harvester that something external changed (e.g., a settings toggle) and the
        /// next polled tick should treat the console as fresh. Cheaper than a synchronous
        /// <see cref="RescanNow"/> when the caller is on a thread / context that may not be safe
        /// to do reflection from.
        /// </summary>
        public static void RequestRescan()
        {
            if (!IsAvailable)
            {
                return;
            }
            // Setting _lastSeenCount to a sentinel forces the next Tick to see a count delta and
            // call RescanNow on the editor's update thread (when LogEntries is wired). When
            // LogEntries is unavailable, Tick is not registered, so we fall back to delayCall.
            if (_logEntriesDisabled)
            {
                ScheduleRescanWhenIdle();
                return;
            }
            _lastSeenCount = -1;
        }

        private static void Tick()
        {
            // Tick is only registered when the LogEntries reflection layer is available, so we
            // do NOT need to re-check _logEntriesDisabled here; but the IsAvailable guard
            // protects against a future failure mode where IsAvailable is flipped to false at
            // runtime.
            if (!IsAvailable)
            {
                return;
            }

            // Defensive belt: never reflect into LogEntries while a compile or asset-import is
            // running. Even though RescanNow() itself bails on this state, we don't want to even
            // call GetCount(); the lock contention is the source of the freeze, and GetCount
            // touches the same buffer.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
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

                int currentCount;
                try
                {
                    currentCount = (int)_getCount.Invoke(null, null);
                }
                catch (Exception ex)
                {
                    LogExceptionOnce("tick-count", "GetCount during Tick failed.", ex);
                    return;
                }

                if (currentCount != _lastSeenCount)
                {
                    RescanNow();
                }
            }
            catch (Exception ex)
            {
                LogExceptionOnce("tick", "Tick failed.", ex);
            }
        }

        private static void OnAssemblyCompilationFinished(
            string assemblyPath,
            CompilerMessage[] messages
        )
        {
            // CRITICAL: this fires for EVERY assembly compiled (10s of times per build). Running
            // RescanNow synchronously here invokes LogEntries reflection while OTHER assemblies
            // are still compiling; the compiler holds its log-buffer lock and our reflection
            // call blocks waiting for it. Combined with AssetDatabase touches inside RescanNow,
            // this caused permanent script-compilation freezes on Unity startup.
            // We DO parse the per-assembly CompilerMessage payload here (cheap, pure-CPU work,
            // no AssetDatabase / LogEntries contact) and stash it in the cross-thread channel
            // without checking settings here. The settings gate requires AssetDatabase access, so
            // RescanNow applies it later after DrainScheduledRescan has requeued until Unity is
            // idle; if the bridge is disabled, RescanNow clears this pending buffer. This is the
            // primary bridge data path on Unity 2021, where Roslyn-analyzer warnings DO arrive in
            // CompilerMessage[] but do NOT reliably appear in the LogEntries store.
            try
            {
                if (!string.IsNullOrEmpty(assemblyPath) && messages != null)
                {
                    List<string> lines = null;
                    foreach (CompilerMessage compilerMessage in messages)
                    {
                        string body = compilerMessage.message;
                        if (string.IsNullOrEmpty(body))
                        {
                            continue;
                        }
                        // Quick prefilter so we don't parse every CS0123 in the build. The
                        // analyzer always emits "DXMSG00" inside the diagnostic id.
                        if (body.IndexOf("DXMSG00", StringComparison.Ordinal) < 0)
                        {
                            continue;
                        }
                        lines ??= new List<string>();
                        lines.Add(body);
                    }

                    Dictionary<string, ParsedTypeReport> aggregated = lines is null
                        ? new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal)
                        : BaseCallLogMessageParser.Aggregate(lines);

                    lock (_compilationFeedLock)
                    {
                        // Even when this assembly produced zero matching messages, we still want
                        // an empty entry so DrainScheduledRescan can RETIRE the assembly's prior
                        // attribution (the user fixed every offending type in this assembly).
                        _pendingByAssembly[assemblyPath] = aggregated;
                    }
                }
            }
            catch (Exception ex)
            {
                LogExceptionOnce(
                    "compilation-parse",
                    $"Failed to parse CompilerMessage payload for {assemblyPath}.",
                    ex
                );
            }

            // Schedule a single delayed drain. The callback rechecks editor state and requeues if
            // Unity is still compiling/updating, so this never drops the final rescan.
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
            // delayCall can fire while still mid-compile if the editor is in a weird state.
            // RescanNow has its own isCompiling/isUpdating guard; re-defer if needed.
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
            // CRITICAL: passive load only. We must NOT call GetOrCreateSettings here; that path
            // can call AssetDatabase.CreateAsset, which during script compilation schedules an
            // import → re-triggers compilation → permanent freeze. The Project Settings page and
            // the inspector overlay both call GetOrCreateSettings on demand (outside compilation),
            // so the asset is materialised through normal user interaction. If the asset doesn't
            // exist yet (fresh project, first compile), the harvester treats the snapshot as
            // unconfigured and behaves as if the master toggle is enabled (default behaviour).
            try
            {
                string[] guids = AssetDatabase.FindAssets($"t:{nameof(DxMessagingSettings)}");
                if (guids == null || guids.Length == 0)
                {
                    return null;
                }
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                if (string.IsNullOrEmpty(assetPath))
                {
                    return null;
                }
                return AssetDatabase.LoadAssetAtPath<DxMessagingSettings>(assetPath);
            }
            catch (Exception ex)
            {
                LogExceptionOnce("settings", "Could not load DxMessagingSettings.", ex);
                return null;
            }
        }

        private static MethodInfo SafeGetStaticMethod(Type type, string name)
        {
            try
            {
                return type.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                LogExceptionOnce(
                    $"resolve-{name}",
                    $"Failed to resolve static method '{name}' on {type.FullName}.",
                    ex
                );
                return null;
            }
        }

        private static FieldInfo SafeGetInstanceField(Type type, string name)
        {
            try
            {
                return type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                LogExceptionOnce(
                    $"resolve-field-{name}",
                    $"Failed to resolve instance field '{name}' on {type.FullName}.",
                    ex
                );
                return null;
            }
        }

        private static void LogOnce(string key, string message)
        {
            if (!AlreadyWarned.Add(key))
            {
                return;
            }
            Debug.LogWarning($"[DxMessaging] {message}");
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

        // -- JSON persistence -----------------------------------------------------------------
        // The cache survives editor restarts so the overlay has data to render before the first
        // post-launch rescan completes; it is rewritten on every successful rescan.

        internal static void PersistToDisk()
        {
            try
            {
                string absolutePath = GetReportFilePath();
                EnsureDirectoryExists(absolutePath);

                BaseCallReportFile file = new()
                {
                    version = 1,
                    generatedAt = DateTime.UtcNow.ToString(
                        "yyyy-MM-ddTHH:mm:ssZ",
                        CultureInfo.InvariantCulture
                    ),
                    types = SnapshotInternal
                        .Values.OrderBy(e => e.typeName, StringComparer.Ordinal)
                        .ToList(),
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
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                .Replace("\\", "/");
            return Path.Combine(projectRoot, "Library", ReportDirectoryName, ReportFileName)
                .Replace("\\", "/");
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
