#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text;
    using NUnit.Framework;
    using UnityEngine;
    using DebuggableAttribute = System.Diagnostics.DebuggableAttribute;

    /// <summary>
    /// Pins the contract that Unity CI loads package assemblies compiled with optimizations.
    /// </summary>
    /// <remarks>
    /// Workflow flags and generated project settings prove only what the harness requested.
    /// Roslyn records what it honored in the assembly-level
    /// <see cref="DebuggableAttribute"/>: optimized assemblies leave
    /// <see cref="DebuggableAttribute.IsJITOptimizerDisabled"/> false. PlayMode inspects the
    /// editor-compiled assemblies used by the in-editor legs; standalone inspects the player build.
    /// </remarks>
    [TestFixture]
    [Category("Fast")]
    [Category("Validation")]
    public sealed class ReleaseCompilationContractTests
    {
        private const string PackageAssemblyPrefix = "WallstopStudios.DxMessaging";
        private const string RuntimeAssemblyName = "WallstopStudios.DxMessaging";

        [Test]
        public void PackageAssembliesAreCompiledWithOptimizationsEnabled()
        {
            List<string> optimized = new();
            List<string> unoptimized = new();
            List<string> unreadable = new();

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try
                {
                    name = assembly.GetName().Name;
                }
                catch (Exception)
                {
                    continue;
                }

                if (
                    string.IsNullOrEmpty(name)
                    || !name.StartsWith(PackageAssemblyPrefix, StringComparison.Ordinal)
                )
                {
                    continue;
                }

                if (!TryReadOptimizerDisabled(assembly, out bool optimizerDisabled))
                {
                    unreadable.Add(name);
                }
                else if (optimizerDisabled)
                {
                    unoptimized.Add(name);
                }
                else
                {
                    optimized.Add(name);
                }
            }

            optimized.Sort(StringComparer.Ordinal);
            unoptimized.Sort(StringComparer.Ordinal);
            unreadable.Sort(StringComparer.Ordinal);

            Assert.That(
                optimized.Contains(RuntimeAssemblyName)
                    || unoptimized.Contains(RuntimeAssemblyName),
                Is.True,
                $"'{RuntimeAssemblyName}' carried no readable DebuggableAttribute, so this "
                    + "contract verified nothing about the runtime package. "
                    + $"Optimized: [{string.Join(", ", optimized)}]. "
                    + $"Unoptimized: [{string.Join(", ", unoptimized)}]. "
                    + $"Unreadable: [{string.Join(", ", unreadable)}]."
            );

            if (!Application.isBatchMode)
            {
                Assert.Ignore(Describe(optimized, unoptimized, enforced: false));
            }

            Assert.That(unoptimized, Is.Empty, Describe(optimized, unoptimized, enforced: true));
        }

        private static bool TryReadOptimizerDisabled(Assembly assembly, out bool optimizerDisabled)
        {
            optimizerDisabled = false;
            object[] attributes;
            try
            {
                attributes = assembly.GetCustomAttributes(typeof(DebuggableAttribute), false);
            }
            catch (Exception)
            {
                return false;
            }

            foreach (object attribute in attributes)
            {
                if (attribute is DebuggableAttribute debuggable)
                {
                    optimizerDisabled = debuggable.IsJITOptimizerDisabled;
                    return true;
                }
            }

            return false;
        }

        private static string Describe(
            IReadOnlyList<string> optimized,
            IReadOnlyList<string> unoptimized,
            bool enforced
        )
        {
            StringBuilder message = new();
            if (enforced)
            {
                message.Append(
                    "Compiled with the optimizer disabled. Unity CI must pass "
                        + "-releaseCodeOptimization for editor compilation and omit "
                        + "BuildOptions.Development for player builds. Offending assemblies: "
                );
            }
            else
            {
                message.Append(
                    "Reported, not enforced: this run is not in batch mode. "
                        + $"{optimized.Count} optimized, {unoptimized.Count} unoptimized"
                );
                if (unoptimized.Count == 0)
                {
                    return message.Append('.').ToString();
                }

                message.Append(" -- ");
            }

            return message.Append(string.Join(", ", unoptimized)).Append('.').ToString();
        }
    }
}
#endif
