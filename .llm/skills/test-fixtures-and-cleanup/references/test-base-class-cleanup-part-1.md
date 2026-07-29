## Overview

Continuation material extracted from `test-base-class-cleanup.md` to keep .llm files within the 300-line budget.

## Solution

### Core Concept

```text
+-----------------------------------------------------------------+
|  CommonTestBase                                                 |
+-----------------------------------------------------------------+
|  List<Object> _trackedObjects                                   |
|  List<IDisposable> _trackedDisposables                          |
+-----------------------------------------------------------------+
|  Track<T>(T obj) -> adds to list, returns obj                   |
|  TrackDisposable<T>(T d) -> adds to list, returns d             |
+-----------------------------------------------------------------+
|  [TearDown] -> Destroys all tracked objects                     |
|  [UnityTearDown] -> Yields for async destruction                |
+-----------------------------------------------------------------+
```

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Base class for Unity tests with automatic resource cleanup.
    /// </summary>
    public abstract class CommonTestBase
    {
        private List<Object> _trackedObjects;
        private List<IDisposable> _trackedDisposables;
        private List<Scene> _trackedScenes;

        /// <summary>
        /// Set to true to defer asset cleanup to OneTimeTearDown.
        /// Useful when tests share expensive assets.
        /// </summary>
        protected virtual bool DeferAssetCleanupToOneTimeTearDown => false;

        [OneTimeSetUp]
        public virtual void CommonOneTimeSetUp()
        {
            _trackedObjects = new List<Object>(16);
            _trackedDisposables = new List<IDisposable>(4);
            _trackedScenes = new List<Scene>(2);
        }

        [SetUp]
        public virtual void CommonSetUp()
        {
            // Clear per-test tracking if not deferring
            if (!DeferAssetCleanupToOneTimeTearDown)
            {
                _trackedObjects.Clear();
                _trackedDisposables.Clear();
            }
        }

        /// <summary>
        /// Track a Unity Object for automatic destruction.
        /// Returns the object for fluent usage.
        /// </summary>
        protected T Track<T>(T obj) where T : Object
        {
            if (obj != null)
            {
                _trackedObjects.Add(obj);
            }
            return obj;
        }

        /// <summary>
        /// Track a disposable for automatic disposal.
        /// </summary>
        protected T TrackDisposable<T>(T disposable) where T : IDisposable
        {
            if (disposable != null)
            {
                _trackedDisposables.Add(disposable);
            }
            return disposable;
        }

        /// <summary>
        /// Create and track a GameObject.
        /// </summary>
        protected GameObject CreateGameObject(string name = "TestObject")
        {
            return Track(new GameObject(name));
        }

        /// <summary>
        /// Create and track a GameObject with components.
        /// </summary>
        protected GameObject CreateGameObject(string name, params Type[] components)
        {
            return Track(new GameObject(name, components));
        }

        /// <summary>
        /// Create and track a new test scene.
        /// </summary>
        protected Scene CreateTestScene(string name = "TestScene")
        {
            Scene scene = SceneManager.CreateScene(name);
            _trackedScenes.Add(scene);
            return scene;
        }

        [TearDown]
        public virtual void TearDown()
        {
            if (DeferAssetCleanupToOneTimeTearDown)
            {
                return;
            }

            CleanupDisposables();
        }

        [UnityTearDown]
        public virtual IEnumerator UnityTearDown()
        {
            if (DeferAssetCleanupToOneTimeTearDown)
            {
                yield break;
            }

            yield return CleanupTrackedObjects();
        }

        [OneTimeTearDown]
        public virtual IEnumerator CommonOneTimeTearDown()
        {
            if (DeferAssetCleanupToOneTimeTearDown)
            {
                CleanupDisposables();
                yield return CleanupTrackedObjects();
            }

            yield return CleanupScenes();
        }

        private void CleanupDisposables()
        {
            foreach (IDisposable disposable in _trackedDisposables)
            {
                try
                {
                    disposable?.Dispose();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CommonTestBase] Dispose failed: {e.Message}");
                }
            }
            _trackedDisposables.Clear();
        }

        private IEnumerator CleanupTrackedObjects()
        {
            // Destroy in reverse order (children before parents)
            for (int i = _trackedObjects.Count - 1; i >= 0; i--)
            {
                Object obj = _trackedObjects[i];
                if (obj != null)
                {
                    if (Application.isPlaying)
                    {
                        Object.Destroy(obj);
                    }
                    else
                    {
                        Object.DestroyImmediate(obj);
                    }
                }
            }
            _trackedObjects.Clear();

            // Yield to allow destruction to complete
            yield return null;
        }

        private IEnumerator CleanupScenes()
        {
            foreach (Scene scene in _trackedScenes)
            {
                if (scene.isLoaded)
                {
                    yield return SceneManager.UnloadSceneAsync(scene);
                }
            }
            _trackedScenes.Clear();
        }
    }
}
```

## See Also

- [Test Base Class with Automatic Resource Cleanup](./test-base-class-cleanup.md)
