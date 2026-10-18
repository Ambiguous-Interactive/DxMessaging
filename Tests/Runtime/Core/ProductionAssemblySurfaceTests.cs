namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    public sealed class ProductionAssemblySurfaceTests
    {
        [Test]
        public void RuntimeAssemblyContainsNoTestOnlyEntryPoints()
        {
            Assert.That(FindTestOnlyEntryPoints(typeof(MessageBus).Assembly), Is.Empty);
        }

        internal static string[] FindTestOnlyEntryPoints(Assembly assembly)
        {
            string[] reservedNames = { "ForTesting", "ForTests", "ForBenchmark", "TestSeams" };
            List<string> found = new();
            Dictionary<string, string[]> retiredSlotReadouts = new()
            {
                ["DxMessaging.Core.MessageHandler+HandlerActionCache`1+OrderedEntries"] = new[]
                {
                    "KeyAt",
                },
                ["DxMessaging.Core.Internal.IHandlerActionCache"] = new[]
                {
                    "Version",
                    "LastSeenVersion",
                    "LastSeenEmissionId",
                },
                ["DxMessaging.Core.MessageHandler+HandlerActionCache`1"] = new[]
                {
                    "Version",
                    "LastSeenVersion",
                    "LastSeenEmissionId",
                },
                ["DxMessaging.Core.MessageBus.Internal.IEvictableSlot"] = new[] { "Version" },
                ["DxMessaging.Core.Internal.TypedSlot`1"] = new[]
                {
                    "Clear",
                    "Version",
                    "lastSeenVersion",
                    "lastSeenEmissionId",
                },
                ["DxMessaging.Core.Internal.TypedGlobalSlot"] = new[]
                {
                    "Clear",
                    "Version",
                    "lastSeenVersion",
                    "lastSeenEmissionId",
                },
                ["DxMessaging.Core.MessageBus.Internal.BusGlobalSlot"] = new[]
                {
                    "Version",
                    "lastSeenVersion",
                    "lastSeenEmissionId",
                    "sharedCache",
                },
            };
            foreach (Type type in assembly.GetTypes())
            {
                if ((type.Namespace ?? string.Empty).Split('.').Contains("Testing"))
                {
                    found.Add(type.FullName);
                }
                foreach (
                    MethodInfo method in type.GetMethods(
                        BindingFlags.Static
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly
                    )
                )
                {
                    if (
                        method.Name == "op_Implicit"
                        && method.ReturnType == typeof(Action)
                        && type.FullName.StartsWith(
                            "DxMessaging.Core.MessageHandler+",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        found.Add(type.FullName + "." + method.Name);
                    }
                }
                foreach (
                    MemberInfo member in type.GetMembers(
                        BindingFlags.Instance
                            | BindingFlags.Static
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly
                    )
                )
                {
                    if (
                        reservedNames.Any(name => member.Name.Contains(name))
                        || (
                            retiredSlotReadouts.TryGetValue(type.FullName, out string[] retired)
                            && retired.Any(name =>
                                member.Name == name
                                || member.Name.EndsWith("." + name, StringComparison.Ordinal)
                            )
                        )
                        || (
                            type.FullName
                                == "DxMessaging.Core.Configuration.DxMessagingRuntimeSettingsProvider"
                            && member.Name == "Override"
                        )
                    )
                    {
                        found.Add(type.FullName + "." + member.Name);
                    }
                }
            }
            return found.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }
    }
}
