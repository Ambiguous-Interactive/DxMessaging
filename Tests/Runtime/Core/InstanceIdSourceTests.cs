#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections.Generic;
    using DxMessaging.Core;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Locks the Unity 6 <c>EntityId</c> migration (GitHub #208). Every dispatch key
    /// is derived from a single version-gated source, <see cref="InstanceId.StableId"/>,
    /// which reads the non-deprecated <c>EntityId.ToULong(...)</c> accessor on Unity 6.4+
    /// and the legacy <c>GetInstanceID()</c> on older Unity. These tests prove the integer
    /// identity is unchanged by the migration (so dispatch routing is byte-for-byte
    /// equivalent) and that no shipped code names the deprecated API outside that one gated
    /// helper.
    /// </summary>
    public sealed class InstanceIdSourceTests
    {
#if !UNITY_6000_5_OR_NEWER
        /// <summary>
        /// The version-gated source must yield the exact integer the dispatch key has
        /// always used, on every <c>UnityEngine.Object</c> kind: the value the legacy
        /// <c>GetInstanceID()</c> returns. Gated out on Unity 6.5+, where
        /// <c>GetInstanceID()</c> is removed (a compile error) and so cannot serve as the
        /// independent reference -- the conversion test and drift-guard still apply there.
        /// </summary>
        [Test]
        public void StableIdMatchesLegacyInstanceIdForEveryUnityObjectKind()
        {
            GameObject gameObject = new(
                nameof(StableIdMatchesLegacyInstanceIdForEveryUnityObjectKind)
            );
            ScriptableObject scriptableObject = ScriptableObject.CreateInstance<ScriptableObject>();
            try
            {
                Component component = gameObject.AddComponent<BoxCollider>();
                UnityEngine.Object[] objects = { gameObject, component, scriptableObject };
                foreach (UnityEngine.Object unityObject in objects)
                {
#pragma warning disable CS0618 // Legacy source the migration preserves (warning pre-6.5; removed in 6.5).
                    int legacy = unityObject.GetInstanceID();
#pragma warning restore CS0618
                    Assert.AreEqual(
                        legacy,
                        InstanceId.StableId(unityObject),
                        "StableId must equal the legacy instance id for {0}.",
                        unityObject.GetType().Name
                    );
                }
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(scriptableObject);
            }
        }
#endif

        /// <summary>
        /// The implicit GameObject/Component to <see cref="InstanceId"/> conversions must
        /// derive their id through <see cref="InstanceId.StableId"/>, so the conversion a
        /// caller actually uses carries the migrated source.
        /// </summary>
        [Test]
        public void InstanceIdConversionUsesStableIdSource()
        {
            GameObject gameObject = new(nameof(InstanceIdConversionUsesStableIdSource));
            try
            {
                InstanceId fromGameObject = gameObject;
                Assert.AreEqual(InstanceId.StableId(gameObject), fromGameObject.Id);

                Component component = gameObject.AddComponent<BoxCollider>();
                InstanceId fromComponent = component;
                Assert.AreEqual(InstanceId.StableId(component), fromComponent.Id);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        /// <summary>
        /// Drift-guard: every shipped <c>UnityEngine.Object</c> id read must funnel through
        /// <see cref="InstanceId.StableId"/>, so the deprecated source call lives in exactly
        /// one version-gated place. The banned identifier is assembled by concatenation so
        /// this contract's own description cannot match it, and only <c>InstanceId.cs</c>
        /// (the gated helper) is allowed to contain it. When the shipped source is not on
        /// disk (a standalone player run) the scan is vacuously satisfied, matching the
        /// other source-scan contracts.
        /// </summary>
        [Test]
        public void ShippedSourceReadsInstanceIdOnlyThroughStableId()
        {
            string bannedIdentifier = "GetInstance" + "ID";
            const string allowedRelativePath = "Runtime/Core/InstanceId.cs";

            List<string> roots = new();
            roots.AddRange(ResolvePackageSubtreeRoots("Runtime"));
            roots.AddRange(ResolvePackageSubtreeRoots("Editor"));

            HashSet<string> scannedFiles = new(System.StringComparer.OrdinalIgnoreCase);
            List<string> offenders = new();

            foreach (string root in roots)
            {
                if (!System.IO.Directory.Exists(root))
                {
                    continue;
                }

                foreach (
                    string file in System.IO.Directory.EnumerateFiles(
                        root,
                        "*.cs",
                        System.IO.SearchOption.AllDirectories
                    )
                )
                {
                    string normalized = System.IO.Path.GetFullPath(file);
                    if (!scannedFiles.Add(normalized))
                    {
                        continue;
                    }

                    // Only the single version-gated source is allowed to name the legacy
                    // API, matched by its full relative path (not just the file name).
                    if (
                        normalized
                            .Replace('\\', '/')
                            .EndsWith(
                                allowedRelativePath,
                                System.StringComparison.OrdinalIgnoreCase
                            )
                    )
                    {
                        continue;
                    }

                    string[] lines;
                    try
                    {
                        lines = System.IO.File.ReadAllLines(file);
                    }
                    catch (System.IO.IOException)
                    {
                        continue;
                    }

                    for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        if (lines[lineIndex].Contains(bannedIdentifier))
                        {
                            offenders.Add(normalized + ":" + (lineIndex + 1));
                        }
                    }
                }
            }

            Assert.That(
                offenders,
                Is.Empty,
                "These shipped files read a Unity object id directly instead of routing "
                    + "through InstanceId.StableId (the one version-gated source for the "
                    + "Unity 6 EntityId migration, GitHub #208):\n"
                    + string.Join("\n", offenders)
            );
        }

        /// <summary>
        /// Best-effort on-disk location of a shipped package subtree (<c>Runtime</c> or
        /// <c>Editor</c>), mirroring the test-source resolver used by the other source-scan
        /// contracts. An empty result (source not on disk) leaves the caller's scan
        /// vacuously satisfied.
        /// </summary>
        private static IEnumerable<string> ResolvePackageSubtreeRoots(string subtree)
        {
            string[] candidates =
            {
                System.IO.Path.Combine(
                    UnityEngine.Application.dataPath,
                    "..",
                    "Packages",
                    "com.wallstop-studios.dxmessaging",
                    subtree
                ),
                System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", subtree),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), subtree),
            };

            List<string> roots = new();
            foreach (string candidate in candidates)
            {
                string full = System.IO.Path.GetFullPath(candidate);
                if (System.IO.Directory.Exists(full))
                {
                    roots.Add(full);
                }
            }

            return roots;
        }
    }
}
#endif
