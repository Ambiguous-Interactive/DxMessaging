namespace DxMessaging.Editor.Settings
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using UnityEditor;
    using UnityEngine;

    /// <summary>
    /// Synchronizes the <see cref="DxMessagingSettings._baseCallIgnoredTypes"/> list to a sidecar
    /// text file shipped to the Roslyn analyzer via <c>csc.rsp</c>'s <c>-additionalfile</c> switch.
    /// </summary>
    /// <remarks>
    /// The sidecar is a build-derived view: never hand-edited, always regenerated from the
    /// ScriptableObject. The analyzer reads it because it cannot parse Unity-serialized YAML.
    /// </remarks>
    public static class DxMessagingBaseCallIgnoreSync
    {
        /// <summary>
        /// Project-relative path to the auto-generated sidecar file. Other Editor scripts
        /// (e.g., <see cref="SetupCscRsp"/>) reference this constant when wiring
        /// <c>-additionalfile</c> entries.
        /// </summary>
        public const string SidecarAssetPath = "Assets/Editor/DxMessaging.BaseCallIgnore.txt";

        private const string HeaderComment =
            "# Auto-generated from Assets/Editor/DxMessagingSettings.asset; edit there instead.";
        private const string FormatComment =
            "# One fully-qualified type name per line. Lines starting with # are comments.";

        /// <summary>
        /// Test seam controlling WHEN deferred regeneration runs. Production schedules the work on
        /// the next editor tick via <see cref="EditorApplication.delayCall"/>; tests substitute a
        /// capturing scheduler so they can assert no synchronous AssetDatabase import happens and
        /// then drive the deferred work deterministically without touching the filesystem.
        /// </summary>
        internal static Action<Action> DeferralScheduler { get; set; } =
            work => EditorApplication.delayCall += () => work();

        /// <summary>
        /// Test seam for Unity editor state. Production uses Unity's update/compile flags; tests
        /// substitute a deterministic busy/idle predicate to prove deferred work requeues.
        /// </summary>
        internal static Func<bool> CanMutateAssetDatabase { get; set; } =
            DxMessaging.Editor.DxMessagingEditorIdle.CanMutateAssetDatabase;

        /// <summary>
        /// Test seam controlling WHAT regeneration does. Production performs the real
        /// write + <see cref="AssetDatabase.ImportAsset"/> via <see cref="RegenerateSidecarCore"/>;
        /// tests substitute a recorder so they can observe whether the apply happens synchronously
        /// or only after the deferred tick, without writing the on-disk sidecar.
        /// </summary>
        internal static Action<DxMessagingSettings> SidecarApplier { get; set; } =
            RegenerateSidecarCore;

        /// <summary>
        /// Test seam for the follow-up <c>csc.rsp</c> sync. Production schedules the
        /// <c>-additionalfile</c> normalization after sidecar regeneration, which covers deferred
        /// <c>OnValidate</c> writes that finish after <see cref="SetupCscRsp"/>'s startup pass.
        /// </summary>
        internal static Action CscRspAdditionalFileSyncScheduler { get; set; } =
            ScheduleDefaultCscRspAdditionalFileSync;

        /// <summary>
        /// Regenerates the sidecar text file from the supplied settings asset. Writes only when
        /// the on-disk content differs from what would be written, to avoid AssetDatabase churn
        /// during domain reload.
        /// </summary>
        /// <param name="settings">The settings asset. May be <c>null</c> -- no-op in that case.</param>
        /// <remarks>
        /// This is the entry point for explicit, user-driven edits (the "Ignore this type" button,
        /// settings asset Inspector edits) and for EditMode tests: when Unity is idle the regen runs
        /// synchronously for immediate feedback, and when Unity is mid-compile or mid-asset-import
        /// it falls back to <see cref="RegenerateSidecarDeferred"/>. It MUST NOT be called from a
        /// deserialization callback such as <c>ScriptableObject.OnValidate</c> -- those callbacks
        /// can fire during the domain-load asset-import-worker window where
        /// <see cref="EditorApplication.isUpdating"/> and <see cref="EditorApplication.isCompiling"/>
        /// are both <c>false</c>, so this method would import synchronously and crash the editor
        /// (issue #210). Use <see cref="RegenerateSidecarDeferred"/> from those callbacks instead.
        /// </remarks>
        public static void RegenerateSidecar(DxMessagingSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            if (!CanCurrentlyMutateAssetDatabase())
            {
                RegenerateSidecarDeferred(settings);
                return;
            }

            ApplySidecarAndScheduleCscRspSync(settings);
        }

        /// <summary>
        /// Schedules sidecar regeneration for the next editor tick, NEVER writing or importing
        /// synchronously. This is the only safe entry point from
        /// <see cref="ScriptableObject.OnValidate"/> and other asset-deserialization callbacks.
        /// </summary>
        /// <param name="settings">The settings asset. May be <c>null</c> -- no-op in that case.</param>
        /// <remarks>
        /// A synchronous <see cref="AssetDatabase.ImportAsset"/> issued from within asset
        /// deserialization re-enters the asset importer and hard-crashes the native editor
        /// (<c>GuidReservations::Reserve</c> abort, issue #210, observed on Unity 6000.4+). The
        /// dangerous window includes the domain-load asset-import worker, during which
        /// <see cref="EditorApplication.isUpdating"/> and <see cref="EditorApplication.isCompiling"/>
        /// are both <c>false</c> -- so the flag-based guard in <see cref="RegenerateSidecar"/> is
        /// not sufficient on its own and this method defers unconditionally. The captured
        /// <paramref name="settings"/> reference is safe to hold: <see cref="EditorApplication.delayCall"/>
        /// is cleared on domain reload, so the callback never outlives its domain, and the
        /// lifetime-aware null check covers the asset being destroyed (deleted) within the domain.
        /// Scheduling here cannot throw, and the deferred regeneration self-guards via
        /// <see cref="RegenerateSidecarCore"/>'s internal try/catch, so callers such as
        /// <c>OnValidate</c> need no surrounding exception guard.
        /// </remarks>
        internal static void RegenerateSidecarDeferred(DxMessagingSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            DxMessaging.Editor.DxMessagingEditorIdle.ScheduleAssetDatabaseMutation(
                () =>
                {
                    if (settings == null)
                    {
                        return;
                    }
                    ApplySidecarAndScheduleCscRspSync(settings);
                },
                DeferralScheduler,
                CanCurrentlyMutateAssetDatabase
            );
        }

        private static bool CanCurrentlyMutateAssetDatabase()
        {
            Func<bool> canMutateAssetDatabase =
                CanMutateAssetDatabase
                ?? DxMessaging.Editor.DxMessagingEditorIdle.CanMutateAssetDatabase;
            return canMutateAssetDatabase();
        }

        private static void ApplySidecarAndScheduleCscRspSync(DxMessagingSettings settings)
        {
            try
            {
                SidecarApplier(settings);
            }
            finally
            {
                ScheduleCscRspAdditionalFileSync();
            }
        }

        private static void ScheduleCscRspAdditionalFileSync()
        {
            try
            {
                Action scheduler =
                    CscRspAdditionalFileSyncScheduler ?? ScheduleDefaultCscRspAdditionalFileSync;
                scheduler();
            }
            catch (Exception ex)
            {
                DxMessaging.Editor.DxMessagingEditorLog.LogWarning(
                    "Failed to schedule csc.rsp base-call ignore additionalfile sync.",
                    ex
                );
            }
        }

        private static void ScheduleDefaultCscRspAdditionalFileSync()
        {
            DxMessaging.Editor.SetupCscRsp.ScheduleAdditionalFileForIgnoreListSync();
        }

        private static void RegenerateSidecarCore(DxMessagingSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            try
            {
                string newContent = BuildContent(settings._baseCallIgnoredTypes);
                string absolutePath = GetAbsolutePath();
                EnsureParentDirectoryExists(absolutePath);

                if (File.Exists(absolutePath))
                {
                    string existing = File.ReadAllText(absolutePath);
                    if (string.Equals(existing, newContent, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                File.WriteAllText(absolutePath, newContent);
                AssetDatabase.ImportAsset(SidecarAssetPath);
            }
            catch (Exception ex)
            {
                DxMessaging.Editor.DxMessagingEditorLog.LogWarning(
                    $"Failed to write base-call ignore sidecar at '{SidecarAssetPath}'.",
                    ex
                );
            }
        }

        /// <summary>
        /// Reads the sidecar file and returns its non-comment, non-blank entries.
        /// Tolerant of a missing file (returns an empty list) and of <c>#</c>-prefixed comment lines.
        /// </summary>
        public static IReadOnlyList<string> ReadSidecar()
        {
            try
            {
                string absolutePath = GetAbsolutePath();
                if (!File.Exists(absolutePath))
                {
                    return Array.Empty<string>();
                }

                List<string> entries = new();
                foreach (string rawLine in File.ReadAllLines(absolutePath))
                {
                    if (string.IsNullOrWhiteSpace(rawLine))
                    {
                        continue;
                    }
                    string trimmed = rawLine.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }
                    entries.Add(trimmed);
                }
                return entries;
            }
            catch (Exception ex)
            {
                DxMessaging.Editor.DxMessagingEditorLog.LogWarning(
                    $"Failed to read base-call ignore sidecar at '{SidecarAssetPath}'.",
                    ex
                );
                return Array.Empty<string>();
            }
        }

        internal static string BuildContent(IList<string> ignoredTypes)
        {
            StringBuilder builder = new();
            builder.Append(HeaderComment).Append('\n');
            builder.Append(FormatComment).Append('\n');

            if (ignoredTypes == null)
            {
                return builder.ToString();
            }

            // Deterministic order for git-friendly diffs; deduplicate while preserving the user's
            // typed casing where possible (Ordinal sort with Ordinal-set dedupe).
            HashSet<string> seen = new(StringComparer.Ordinal);
            List<string> sorted = new(ignoredTypes.Count);
            foreach (string entry in ignoredTypes)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }
                string trimmed = entry.Trim();
                if (seen.Add(trimmed))
                {
                    sorted.Add(trimmed);
                }
            }
            sorted.Sort(StringComparer.Ordinal);

            foreach (string entry in sorted)
            {
                builder.Append(entry).Append('\n');
            }
            return builder.ToString();
        }

        private static string GetAbsolutePath()
        {
            // Application.dataPath ends in "/Assets"; SidecarAssetPath begins with "Assets/".
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                .Replace("\\", "/");
            return Path.Combine(projectRoot, SidecarAssetPath).Replace("\\", "/");
        }

        private static void EnsureParentDirectoryExists(string absolutePath)
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
