#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Core
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;

    internal static class LoadedAssemblyUtility
    {
        internal static IReadOnlyList<Assembly> GetAssemblies()
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
#else
            return AppDomain.CurrentDomain.GetAssemblies();
#endif
        }

        internal static string GetAssemblyPath(Assembly assembly)
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.AssemblyExtension.GetLoadedAssemblyPath(assembly);
#else
            return assembly.Location;
#endif
        }
    }
}
#endif
