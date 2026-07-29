# Auto-Load Singleton Attribute

> **One-line summary**: Auto-loading singleton bootstrapping for Unity scenes.

## Overview

This skill explains auto-load singleton bootstrapping in Unity.

## Solution

Use the attribute and loader pattern below to ensure early initialization.

### AutoLoadSingleton Attribute

```csharp
namespace WallstopStudios.UnityHelpers.Utils
{
    using System;

    /// <summary>
    /// Apply to RuntimeSingleton to auto-create on game start.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class AutoLoadSingletonAttribute : Attribute
    {
    }
}

// RuntimeInitializeOnLoad handler
public static class SingletonAutoLoader
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoLoadSingletons()
    {
        // Find all types with AutoLoadSingleton attribute
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(AutoLoadSingletonAttribute), true).Length > 0)
                {
                    // Access Instance property to trigger creation
                    var instanceProperty = type.GetProperty("Instance",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    instanceProperty?.GetValue(null);
                }
            }
        }
    }
}
```
