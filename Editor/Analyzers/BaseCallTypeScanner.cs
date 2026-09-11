namespace DxMessaging.Editor.Analyzers
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using DxMessaging.Editor.Settings;
    using DxMessaging.Unity;
    using UnityEditor;

    /// <summary>
    /// Edit-time scanner that walks loaded <see cref="MessageAwareComponent"/> subclasses via
    /// <see cref="TypeCache"/> and forwards them to the pure (Unity-API-free) classification
    /// helper <see cref="BaseCallTypeScannerCore"/>. Replaces the lossy console-scraping bridge
    /// as the inspector overlay's primary data source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why IL reflection?</b> Unity's <c>CompilationPipeline.assemblyCompilationFinished</c> and
    /// the <c>LogEntries</c> console store are both downstream of Unity's decision to actually
    /// surface analyzer warnings. On Bee/csc cache hits -- which happen on most domain reloads
    /// after the first -- Unity skips that surface entirely, so the scrape returns nothing even
    /// though the analyzer ran successfully on the first compile. By contrast, IL reflection over
    /// loaded types is deterministic: the assemblies are in the AppDomain, the methods have IL
    /// bodies, the same scan produces the same result on every reload regardless of whether the
    /// build was a fresh compile or a Bee cache hit.
    /// </para>
    /// <para>
    /// <b>What it detects:</b>
    /// <list type="bullet">
    /// <item><description>DXMSG006 -- overrides one of the five guarded methods but the IL body
    /// lacks a <c>call</c>/<c>callvirt</c> to the parent's same-named method.</description></item>
    /// <item><description>DXMSG007 -- declares the method with the <c>new</c> modifier (IL: name
    /// shadows a base virtual but the descendant method itself is not in an override slot).</description></item>
    /// <item><description>DXMSG009 -- declares the method without override or new -- same IL shape
    /// as DXMSG007 (both compile to a non-virtual hide-by-sig method). The scanner cannot
    /// distinguish the two perfectly from IL alone, so it conservatively classifies this case as
    /// DXMSG007. The compile-time analyzer is authoritative for the precise ID classification;
    /// the scanner's job is just to make sure the inspector overlay lights up.</description></item>
    /// <item><description>DXMSG010 -- overrides correctly (calls base) but an intermediate
    /// ancestor's override in the chain does NOT call base. Walks parent-by-parent and re-runs the
    /// IL check at every link until the chain terminates at <see cref="MessageAwareComponent"/>
    /// or hits a broken link.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Cross-assembly assume-clean:</b> ancestors whose IL is unavailable
    /// (<see cref="System.Reflection.MethodBase.GetMethodBody"/> returns null -- e.g., abstract or
    /// extern methods) are trusted. Emitting an unactionable warning against a closed-source
    /// third-party library would be hostile.
    /// </para>
    /// <para>
    /// <b>Implementation split:</b> the Unity-coupled work (<see cref="TypeCache"/> lookup,
    /// <see cref="DxMessagingSettings"/> read, conversion to the Unity-serializable
    /// <see cref="BaseCallReportEntry"/>) lives here; everything else (chain walk, IL probe,
    /// FQN normalisation, opt-out handling) lives in <see cref="BaseCallTypeScannerCore"/> so the
    /// dotnet-test project can cover the classification logic via Roslyn-compiled fixtures.
    /// </para>
    /// </remarks>
    internal static class BaseCallTypeScanner
    {
        /// <summary>
        /// Scan all loaded <see cref="MessageAwareComponent"/> subclasses and return a per-type
        /// report keyed by fully-qualified type name. The report shape matches what the
        /// console-bridge produced, so the inspector overlay code path needs no changes.
        /// </summary>
        /// <remarks>
        /// Types opted out via <c>[DxIgnoreMissingBaseCall]</c> or via the project's ignored-types
        /// list are intentionally NOT included in the returned dictionary -- the overlay reads the
        /// project ignore list directly to render its "Stop ignoring" HelpBox, and the snapshot
        /// semantics here match the bridge path (DXMSG008-equivalent rows were never present in
        /// the snapshot's <c>missingBaseFor</c> either).
        /// </remarks>
        internal static Dictionary<string, BaseCallReportEntry> Scan(DxMessagingSettings settings)
        {
            /*
                TypeCache is Unity's domain-reload-cached type lookup. Effectively O(1) after the
                first call and survives across reloads via Unity's serialization layer. Using
                TypeCache (rather than scanning every loaded assembly via AppDomain) is important
                for performance; a fresh project can have hundreds of assemblies loaded.
            */
            TypeCache.TypeCollection candidates =
                TypeCache.GetTypesDerivedFrom<MessageAwareComponent>();

            /*
                Defensive: TypeCache.GetTypesDerivedFrom<T>() returns strict subclasses, but
                belt-and-braces in case a future Unity version changes the contract; we feed the
                list through Core.Scan which itself skips MessageAwareComponent by FQN match.
                The Core handles abstract / generic-definition / null-FQN skipping uniformly.
            */
            Dictionary<string, BaseCallTypeScannerCore.ScanEntry> coreResult =
                BaseCallTypeScannerCore.Scan(
                    candidates,
                    settings != null ? settings._baseCallIgnoredTypes : null
                );

            Dictionary<string, BaseCallReportEntry> result = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, BaseCallTypeScannerCore.ScanEntry> kvp in coreResult)
            {
                BaseCallTypeScannerCore.ScanEntry core = kvp.Value;
                BaseCallReportEntry entry = new()
                {
                    typeName = core.TypeName,
                    missingBaseFor = new List<string>(core.MissingBaseFor),
                    diagnosticIds = new List<string>(core.DiagnosticIds),
                    filePath = string.Empty,
                    line = 0,
                };
                result[kvp.Key] = entry;
            }
            return result;
        }
    }
#endif
}
