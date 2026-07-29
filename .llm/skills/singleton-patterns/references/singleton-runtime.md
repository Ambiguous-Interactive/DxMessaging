# Runtime Singleton Pattern

> **One-line summary**: Runtime singleton implementation with lazy instance creation.

## Overview

This skill focuses on runtime singleton patterns for MonoBehaviours.

## Solution

Apply the implementation below to enforce a single instance safely.

### RuntimeSingleton<T>

```csharp
namespace WallstopStudios.UnityHelpers.Utils
{
    using UnityEngine;

    /// <summary>
    /// Thread-safe MonoBehaviour singleton that persists across scenes.
    /// </summary>
    public abstract class RuntimeSingleton<T> : MonoBehaviour where T : RuntimeSingleton<T>
    {
        private static T instance;
        private static readonly object lockObject = new object();
        private static bool applicationIsQuitting;

        /// <summary>
        /// Whether to call DontDestroyOnLoad. Override to return false for scene-scoped singletons.
        /// </summary>
        protected virtual bool Preserve => true;

        /// <summary>
        /// Gets the singleton instance. Creates one if needed.
        /// </summary>
        public static T Instance
        {
            get
            {
                if (applicationIsQuitting)
                {
                    Debug.LogWarning($"[RuntimeSingleton] Instance of {typeof(T)} requested after application quit.");
                    return null;
                }

                lock (lockObject)
                {
                    if (instance == null)
                    {
                        instance = FindObjectOfType<T>();

                        if (instance == null)
                        {
                            GameObject singletonObject = new GameObject($"[{typeof(T).Name}]");
                            instance = singletonObject.AddComponent<T>();
                        }
                    }

                    return instance;
                }
            }
        }

        /// <summary>
        /// Returns true if an instance exists (without creating one).
        /// </summary>
        public static bool HasInstance
        {
            get
            {
                lock (lockObject)
                {
                    return instance != null;
                }
            }
        }

        protected virtual void Awake()
        {
            lock (lockObject)
            {
                if (instance == null)
                {
                    instance = (T)this;

                    if (Preserve)
                    {
                        transform.SetParent(null); // Ensure not child of another object
                        DontDestroyOnLoad(gameObject);
                    }

                    OnSingletonAwake();
                }
                else if (instance != this)
                {
                    Debug.LogWarning($"[RuntimeSingleton] Duplicate {typeof(T).Name} destroyed on {gameObject.name}");
                    Destroy(gameObject);
                }
            }
        }

        /// <summary>
        /// Called when this instance becomes the singleton. Override instead of Awake().
        /// </summary>
        protected virtual void OnSingletonAwake()
        {
        }

        protected virtual void OnApplicationQuit()
        {
            applicationIsQuitting = true;
        }

        protected virtual void OnDestroy()
        {
            lock (lockObject)
            {
                if (instance == this)
                {
                    instance = null;
                }
            }
        }
    }
}
```
