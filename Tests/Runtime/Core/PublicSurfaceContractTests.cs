#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;
    using Debug = UnityEngine.Debug;

    /// <summary>
    /// Pins invariants on the DxMessaging public surface. The fixture is
    /// intentionally read-only (it does NOT touch the bus during the
    /// tests), so it can run in the default suite without a noticeable
    /// wall-clock cost.
    /// </summary>
    public sealed class PublicSurfaceContractTests
    {
        private const string SnapshotResourceFolderName = "Snapshots";
        private const string PublicSurfaceSnapshotFileName = "public-surface.txt";

        private const string CoreNamespacePrefix = "DxMessaging.Core";
        private const string UnityNamespacePrefix = "DxMessaging.Unity";

        private static readonly string[] EffectivelyPublicNamespacePrefixes =
        {
            CoreNamespacePrefix,
            UnityNamespacePrefix,
        };

        /// <summary>
        /// Enumerates every <c>public</c> type in the
        /// <c>DxMessaging.Core</c> namespace (and its sub-namespaces) and
        /// compares the sorted list to a stored snapshot. The snapshot file
        /// is generated on first run if missing; subsequent runs fail when
        /// the type list drifts. The diff format is one fully-qualified
        /// name per line so a failing test points directly at the
        /// added/removed type.
        /// </summary>
        [Test]
        public void PublicTypeSetInDxMessagingCoreNamespaceMatchesSnapshot()
        {
            List<string> live = EnumeratePublicCoreTypeNames();
            string liveSnapshot = string.Join("\n", live);

            string snapshotPath = TryResolveSnapshotPath(out bool snapshotPathResolved);

            if (!snapshotPathResolved)
            {
                Assert.Inconclusive(
                    "Could not resolve the public-surface snapshot path. The Tests/Runtime/Core/Snapshots "
                        + "directory must exist in the package source tree (it is excluded from build "
                        + "but not from the editor's reflection of test assemblies). Skipping the diff check."
                );
                return;
            }

            if (!File.Exists(snapshotPath))
            {
                // Auto-generate the snapshot for the developer's convenience, then
                // FAIL the test so the snapshot is not silently rubber-stamped.
                File.WriteAllText(snapshotPath, liveSnapshot, new UTF8Encoding(false));
                Assert.Fail(
                    "Public surface snapshot was missing and has been auto-generated at "
                        + $"{snapshotPath}. Run the test once locally, review the auto-generated "
                        + "snapshot (and its .meta sibling), and commit it. The test will only "
                        + "pass with a committed snapshot."
                );
                return;
            }

            string stored = File.ReadAllText(snapshotPath).Replace("\r\n", "\n").TrimEnd();
            string actual = liveSnapshot.TrimEnd();

            if (string.Equals(stored, actual, StringComparison.Ordinal))
            {
                return;
            }

            HashSet<string> storedSet = new HashSet<string>(
                stored.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal
            );
            HashSet<string> actualSet = new HashSet<string>(
                actual.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal
            );

            List<string> added = actualSet
                .Except(storedSet)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            List<string> removed = storedSet
                .Except(actualSet)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            string addedTxt = added.Count == 0 ? "(none)" : string.Join("\n  ", added);
            string removedTxt = removed.Count == 0 ? "(none)" : string.Join("\n  ", removed);

            Assert.Fail(
                $"DxMessaging.Core public surface drift detected. Snapshot file: {snapshotPath}\n"
                    + $"Added types:\n  {addedTxt}\n"
                    + $"Removed types:\n  {removedTxt}\n"
                    + "If the change is intentional, regenerate the snapshot by deleting the file and re-running this test."
            );
        }

        /// <summary>
        /// Asserts that every type considered "public" by the snapshot
        /// enumerator has an effectively public access chain across both the
        /// <c>DxMessaging.Core</c> and <c>DxMessaging.Unity</c> assemblies
        /// (including <c>DxMessaging.Unity.Integrations.Reflex</c>,
        /// <c>DxMessaging.Unity.Integrations.VContainer</c>, and
        /// <c>DxMessaging.Unity.Integrations.Zenject</c>). A nested
        /// <c>public</c> struct inside an <c>internal</c> outer class is
        /// flagged by <see cref="Type.IsNestedPublic"/> as visible, but its
        /// effective accessibility is internal because the outer class clamps
        /// it down. This test pins the invariant directly so the snapshot diff
        /// only ever surfaces genuinely public-API drift, not declared-public
        /// types that the CLR cannot actually surface to consumers.
        /// </summary>
        /// <remarks>
        /// Motivating bug: the original
        /// <c>DxMessaging.Core.DataStructure.CyclicBuffer&lt;T&gt;.CyclicBufferEnumerator</c>
        /// struct was declared <c>public</c> inside an <c>internal sealed
        /// class CyclicBuffer&lt;T&gt;</c>. The CLR clamps the effective
        /// accessibility of the nested struct to internal (matching the
        /// outer), but reflection still reports <c>IsNestedPublic == true</c>,
        /// so the snapshot enumerator and any other naive
        /// <c>IsPublic || IsNestedPublic</c> filter would silently surface it
        /// as part of the public API. The fix changed the nested struct to
        /// <c>internal</c>; this test exists to make the bug class
        /// undetectable-by-eye but always-detectable-by-CI.
        /// </remarks>
        [Test]
        public void NoEffectivelyInternalTypesLeakAsPublic()
        {
            HashSet<string> offenders = new HashSet<string>(StringComparer.Ordinal);

            foreach (string namespacePrefix in EffectivelyPublicNamespacePrefixes)
            {
                foreach (Type type in EnumerateNamespaceTypes(namespacePrefix))
                {
                    // Find types that LOOK public to a naive enumerator but
                    // are actually clamped down by an internal enclosing type.
                    bool looksPublic = type.IsPublic || type.IsNestedPublic;
                    if (!looksPublic)
                    {
                        continue;
                    }

                    if (!IsEffectivelyPublic(type))
                    {
                        string fullName = type.FullName ?? type.Name;
                        offenders.Add(fullName);
                    }
                }
            }

            List<string> sorted = offenders.OrderBy(n => n, StringComparer.Ordinal).ToList();

            Assert.That(
                sorted,
                Is.Empty,
                "Found types declared 'public' inside a non-public enclosing type. The CLR exposes them "
                    + "as IsNestedPublic, but their effective accessibility is clamped down by the outer. "
                    + "Either mark the inner type 'internal' to match the enclosing scope, OR promote the "
                    + "enclosing type to 'public' if its surface is intended to be exposed:\n  "
                    + string.Join("\n  ", sorted)
            );
        }

        /// <summary>
        /// Pins the behavior of <see cref="IsEffectivelyPublic"/> against a
        /// matrix of synthetic types so that a future refactor cannot quietly
        /// turn the helper into a constant-true/constant-false function and
        /// silently disable <see cref="NoEffectivelyInternalTypesLeakAsPublic"/>.
        /// </summary>
        [Test]
        public void IsEffectivelyPublicCorrectlyDetectsKnownPatterns()
        {
            // Top-level public type from the BCL.
            Assert.IsTrue(
                IsEffectivelyPublic(typeof(string)),
                "string is a top-level public BCL type and must be effectively public."
            );

            // Top-level internal type declared in this assembly.
            Assert.IsFalse(
                IsEffectivelyPublic(typeof(SomeInternalClass)),
                "SomeInternalClass is a top-level internal type and must NOT be effectively public."
            );

            // Nested public inside nested public inside top-level public => public.
            Assert.IsTrue(
                IsEffectivelyPublic(typeof(SyntheticOuterPublic.SyntheticInnerPublic)),
                "Nested public inside top-level public must be effectively public."
            );

            // Nested public inside top-level internal outer => NOT public.
            // This is the exact shape of the original
            // CyclicBuffer<T>.CyclicBufferEnumerator leak.
            Assert.IsFalse(
                IsEffectivelyPublic(typeof(SyntheticOuterInternal.SyntheticInnerPublic)),
                "Nested public inside a top-level internal outer must NOT be effectively public."
            );

            // Three-level deep: outer public, middle non-public, inner public
            // => NOT public. This exercises the loop's mid-chain break.
            Assert.IsFalse(
                IsEffectivelyPublic(
                    typeof(SyntheticOuterPublicLevel1.SyntheticMiddleInternal.SyntheticInnerPublic)
                ),
                "A 3-level chain where any middle rung is non-public must NOT be effectively public."
            );
        }

        /// <summary>
        /// Enumerates every method on <see cref="IMessageBus"/> and asserts
        /// each method name appears at least once as a textual token in the
        /// runtime test source tree. This is intentionally a substring grep:
        /// the test confirms each method is mentioned somewhere in the
        /// fixtures (which is a strong proxy that someone has invoked or
        /// referenced it), not that every method is exercised by a real
        /// invocation inside a <c>[Test]</c>/<c>[UnityTest]</c> body. A
        /// stronger structural check would require Roslyn (not currently a
        /// dependency of the runtime test asmdef) or a runtime invocation
        /// trace; until that lands, the substring grep is our pragmatic
        /// floor.
        /// </summary>
        /// <remarks>
        /// The historical name of this test was
        /// <c>EveryIMessageBusMethodHasAtLeastOneTest</c>; the rename clarifies
        /// what is actually being checked so failure messages do not overstate
        /// coverage.
        /// </remarks>
        [Test]
        public void EveryIMessageBusMethodIsTextuallyMentioned()
        {
            HashSet<string> methodNames = new HashSet<string>(
                typeof(IMessageBus)
                    .GetMethods(
                        BindingFlags.Public
                            | BindingFlags.Instance
                            | BindingFlags.DeclaredOnly
                            | BindingFlags.Static
                    )
                    .Where(m => !m.IsSpecialName)
                    .Select(m => m.Name),
                StringComparer.Ordinal
            );

            // Walk the source files for the test assembly. We keep a list
            // of search roots: the parent of one well-known test fixture's
            // file (resolved from the running assembly metadata).
            List<string> searchRoots = ResolveTestSourceRoots();
            if (searchRoots.Count == 0)
            {
                Assert.Inconclusive(
                    "Could not resolve any test source root for IMessageBus method coverage check."
                );
                return;
            }

            HashSet<string> covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (string root in searchRoots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (
                    string file in Directory.EnumerateFiles(
                        root,
                        "*.cs",
                        SearchOption.AllDirectories
                    )
                )
                {
                    string text;
                    try
                    {
                        text = File.ReadAllText(file);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    foreach (string name in methodNames)
                    {
                        if (covered.Contains(name))
                        {
                            continue;
                        }

                        if (text.IndexOf(name, StringComparison.Ordinal) >= 0)
                        {
                            covered.Add(name);
                        }
                    }
                }
            }

            List<string> missing = methodNames
                .Except(covered)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            Assert.That(
                missing,
                Is.Empty,
                "IMessageBus methods with no textual mention anywhere in the test source tree (this is "
                    + "a substring grep, not a structural call-site check):\n  "
                    + string.Join("\n  ", missing)
            );
        }

        /// <summary>
        /// Pins the canonical three-kind source used by tests that deliberately
        /// cover only the context-bound dispatch surfaces.
        /// </summary>
        [Test]
        public void EveryCanonicalMessageKindAppearsInAllKinds()
        {
            HashSet<MessageKind> covered = new HashSet<MessageKind>(
                MessageScenarios.AllKinds.Select(scenario => scenario.Kind)
            );

            MessageKind[] canonicalKinds =
            {
                MessageKind.Untargeted,
                MessageKind.Targeted,
                MessageKind.Broadcast,
            };

            List<string> missing = canonicalKinds
                .Where(kind => !covered.Contains(kind))
                .Select(kind => kind.ToString())
                .ToList();
            List<string> unexpected = covered
                .Except(canonicalKinds)
                .Select(kind => kind.ToString())
                .ToList();

            Assert.That(
                missing,
                Is.Empty,
                "MessageScenarios.AllKinds must yield the canonical context-bound MessageKind values. Missing: "
                    + string.Join(", ", missing)
                    + ". Actual: "
                    + string.Join(", ", covered)
            );
            Assert.That(
                unexpected,
                Is.Empty,
                "MessageScenarios.AllKinds must stay limited to canonical context-bound MessageKind values. Unexpected: "
                    + string.Join(", ", unexpected)
                    + ". Use MessageScenarios.AllKindsIncludingWithoutContext for the full dispatch surface."
            );
        }

        /// <summary>
        /// Enumerates <see cref="MessageKind"/> directly and asserts every value
        /// appears in the source intended for full dispatch-surface coverage.
        /// </summary>
        [Test]
        public void EveryMessageKindAppearsInAllKindsIncludingWithoutContext()
        {
            HashSet<MessageKind> covered = new HashSet<MessageKind>(
                MessageScenarios.AllKindsIncludingWithoutContext.Select(scenario => scenario.Kind)
            );

            List<string> missing = Enum.GetValues(typeof(MessageKind))
                .Cast<MessageKind>()
                .Where(kind => !covered.Contains(kind))
                .Select(kind => kind.ToString())
                .ToList();

            Assert.That(
                missing,
                Is.Empty,
                "MessageScenarios.AllKindsIncludingWithoutContext must yield every MessageKind. Missing: "
                    + string.Join(", ", missing)
                    + ". Actual: "
                    + string.Join(", ", covered)
            );
        }

        private static List<string> EnumeratePublicCoreTypeNames()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> names = new List<string>();

            foreach (Type type in EnumerateNamespaceTypes(CoreNamespacePrefix))
            {
                if (!IsEffectivelyPublic(type))
                {
                    continue;
                }

                string fullName = type.FullName;
                if (string.IsNullOrEmpty(fullName))
                {
                    continue;
                }

                if (seen.Add(fullName))
                {
                    names.Add(fullName);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>
        /// Yields every reflectable type in every loaded assembly whose
        /// namespace is exactly <paramref name="namespacePrefix"/> or starts
        /// with <c><paramref name="namespacePrefix"/> + "."</c>. Types whose
        /// declaring assembly throws <see cref="ReflectionTypeLoadException"/>
        /// are partially recovered (non-null entries from
        /// <see cref="ReflectionTypeLoadException.Types"/>); types with a null
        /// namespace are skipped. Centralizing this loop eliminates duplicated
        /// reflection try/catch blocks across the fixture.
        /// </summary>
        /// <summary>
        /// Snapshot of every reflectable type across all loaded assemblies,
        /// computed once per domain. The full <see cref="AppDomain"/> walk plus
        /// per-assembly <see cref="Assembly.GetTypes"/> is the single most
        /// expensive operation in this fixture; several <c>[Test]</c> methods
        /// call <see cref="EnumerateNamespaceTypes"/>, so caching the walk
        /// collapses N full reflection sweeps into one. The set of loaded
        /// assemblies and their types is stable for the lifetime of the test
        /// domain (test assemblies are all loaded before any test runs), so the
        /// snapshot is safe to reuse; a domain reload (recompile) resets the
        /// static and the snapshot is rebuilt on next access.
        /// </summary>
        private static readonly Lazy<Type[]> AllReflectableTypes = new(CollectAllReflectableTypes);

        private static Type[] CollectAllReflectableTypes()
        {
            List<Type> all = new();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (Type type in types)
                {
                    if (type != null)
                    {
                        all.Add(type);
                    }
                }
            }

            return all.ToArray();
        }

        private static IEnumerable<Type> EnumerateNamespaceTypes(string namespacePrefix)
        {
            string prefixWithDot = namespacePrefix + ".";

            foreach (Type type in AllReflectableTypes.Value)
            {
                if (type.Namespace == null)
                {
                    continue;
                }

                if (
                    !type.Namespace.Equals(namespacePrefix, StringComparison.Ordinal)
                    && !type.Namespace.StartsWith(prefixWithDot, StringComparison.Ordinal)
                )
                {
                    continue;
                }

                yield return type;
            }
        }

        /// <summary>
        /// Returns true if <paramref name="type"/> is effectively part of the
        /// public surface, i.e. every enclosing type in the chain is itself
        /// public. A nested-public type inside an <c>internal</c> outer is
        /// <em>not</em> effectively public, even though
        /// <see cref="Type.IsNestedPublic"/> reports true. The CLR exposes the
        /// declared accessibility on each rung of the nesting ladder, but the
        /// effective accessibility of a type is the minimum of every rung.
        /// </summary>
        /// <remarks>
        /// Pinned by
        /// <see cref="IsEffectivelyPublicCorrectlyDetectsKnownPatterns"/> so
        /// future refactors of this helper cannot silently change behavior
        /// and disable <see cref="NoEffectivelyInternalTypesLeakAsPublic"/>.
        /// The motivating bug was the original
        /// <c>DxMessaging.Core.DataStructure.CyclicBuffer&lt;T&gt;.CyclicBufferEnumerator</c>
        /// leak; see <see cref="NoEffectivelyInternalTypesLeakAsPublic"/> for
        /// the full historical context.
        /// </remarks>
        private static bool IsEffectivelyPublic(Type type)
        {
            if (type == null)
            {
                return false;
            }

            // Fast path: top-level internals and nested non-publics
            // short-circuit immediately so the common case (most types in a
            // closed-over assembly) does not pay for the walk-up loop. This
            // restores parity with the original 'IsPublic || IsNestedPublic'
            // pre-filter that this helper replaced.
            if (!type.IsPublic && !type.IsNestedPublic)
            {
                return false;
            }

            // Walk outward to the top-level type. Each non-top-level rung must
            // be IsNestedPublic; the top-level rung must be IsPublic.
            Type current = type;
            while (current.IsNested)
            {
                if (!current.IsNestedPublic)
                {
                    return false;
                }

                current = current.DeclaringType;
                Debug.Assert(
                    current != null,
                    "A nested type must have a declaring type in a correct CLR; this is unreachable."
                );
            }

            return current.IsPublic;
        }

        private static string TryResolveSnapshotPath(out bool resolved)
        {
            // Walk up from the running assembly's location toward the
            // package root. The snapshot lives at
            // Tests/Runtime/Core/Snapshots/public-surface.txt relative to
            // the package's content root. Application.dataPath is the
            // Unity project's Assets/, but the package may be a
            // package-manager package outside of Assets/, so we instead
            // probe for the well-known directory chain.
            string[] candidates =
            {
                Path.Combine(
                    Application.dataPath,
                    "..",
                    "Packages",
                    "com.wallstop-studios.dxmessaging",
                    "Tests",
                    "Runtime",
                    "Core",
                    SnapshotResourceFolderName,
                    PublicSurfaceSnapshotFileName
                ),
                Path.Combine(
                    Application.dataPath,
                    "..",
                    "Tests",
                    "Runtime",
                    "Core",
                    SnapshotResourceFolderName,
                    PublicSurfaceSnapshotFileName
                ),
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "Tests",
                    "Runtime",
                    "Core",
                    SnapshotResourceFolderName,
                    PublicSurfaceSnapshotFileName
                ),
            };

            foreach (string candidate in candidates)
            {
                string full = Path.GetFullPath(candidate);
                string parent = Path.GetDirectoryName(full);
                if (parent != null && Directory.Exists(parent))
                {
                    resolved = true;
                    return full;
                }
            }

            resolved = false;
            return string.Empty;
        }

        private static List<string> ResolveTestSourceRoots()
        {
            List<string> roots = new List<string>();
            string[] candidates =
            {
                Path.Combine(
                    Application.dataPath,
                    "..",
                    "Packages",
                    "com.wallstop-studios.dxmessaging",
                    "Tests",
                    "Runtime"
                ),
                Path.Combine(Application.dataPath, "..", "Tests", "Runtime"),
                Path.Combine(Directory.GetCurrentDirectory(), "Tests", "Runtime"),
            };

            foreach (string candidate in candidates)
            {
                string full = Path.GetFullPath(candidate);
                if (Directory.Exists(full))
                {
                    roots.Add(full);
                }
            }

            return roots;
        }
    }

    /// <summary>
    /// Synthetic top-level <c>internal</c> sentinel used by
    /// <see cref="PublicSurfaceContractTests.IsEffectivelyPublicCorrectlyDetectsKnownPatterns"/>
    /// to verify that the helper rejects top-level internal types. Lives in
    /// the test asmdef so it does not affect the
    /// <c>DxMessaging.Core</c>/<c>DxMessaging.Unity</c> public surface scan.
    /// </summary>
    internal sealed class SomeInternalClass { }

    /// <summary>
    /// Synthetic top-level <c>public</c> outer used by
    /// <see cref="PublicSurfaceContractTests.IsEffectivelyPublicCorrectlyDetectsKnownPatterns"/>.
    /// Lives in the test asmdef under
    /// <c>DxMessaging.Tests.Runtime.Core</c>, which is not a scanned
    /// production namespace, so adding it does NOT affect the snapshot.
    /// </summary>
    public static class SyntheticOuterPublic
    {
        /// <summary>Inner type for the all-public-chain assertion.</summary>
        public class SyntheticInnerPublic { }
    }

    /// <summary>
    /// Synthetic top-level <c>internal</c> outer used by
    /// <see cref="PublicSurfaceContractTests.IsEffectivelyPublicCorrectlyDetectsKnownPatterns"/>.
    /// Models the original <c>CyclicBuffer&lt;T&gt;.CyclicBufferEnumerator</c>
    /// leak shape: a <c>public</c> nested type inside an <c>internal</c>
    /// top-level outer. <see cref="SyntheticInnerPublic"/> reports
    /// <c>IsNestedPublic == true</c>, but its effective accessibility is
    /// internal because the outer is internal.
    /// </summary>
    internal static class SyntheticOuterInternal
    {
        /// <summary>Inner type for the broken-chain assertion.</summary>
        public class SyntheticInnerPublic { }
    }

    /// <summary>
    /// Synthetic three-level chain used by
    /// <see cref="PublicSurfaceContractTests.IsEffectivelyPublicCorrectlyDetectsKnownPatterns"/>.
    /// Outer is public, middle is internal-nested, inner is nested-public; the
    /// middle rung breaks the chain so the deepest type is NOT effectively
    /// public. Pins the loop's mid-chain rejection branch.
    /// </summary>
    public static class SyntheticOuterPublicLevel1
    {
        /// <summary>
        /// Internal-nested middle rung that breaks the chain.
        /// </summary>
        internal static class SyntheticMiddleInternal
        {
            /// <summary>Deepest rung; declared public but unreachable.</summary>
            public class SyntheticInnerPublic { }
        }
    }
}
#endif
