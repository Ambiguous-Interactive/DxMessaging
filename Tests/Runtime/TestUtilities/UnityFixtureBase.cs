#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;
    using NUnit.Framework;

    /// <summary>
    /// Provides cleanup helpers for tests that instantiate Unity objects or disposable resources.
    /// </summary>
    public abstract class UnityFixtureBase
    {
        private readonly List<IDisposable> _disposables = new();
        private readonly List<UnityEngine.Object> _unityObjects = new();

        protected T Track<T>(T unityObject)
            where T : UnityEngine.Object
        {
            if (unityObject != null)
            {
                _unityObjects.Add(unityObject);
            }

            return unityObject;
        }

        protected T TrackDisposable<T>(T disposable)
            where T : IDisposable
        {
            if (disposable != null)
            {
                _disposables.Add(disposable);
            }

            return disposable;
        }

        [TearDown]
        public virtual void TearDownManagedResources()
        {
            for (int i = _disposables.Count - 1; 0 <= i; i--)
            {
                try
                {
                    _disposables[i]?.Dispose();
                }
                catch
                {
                    // Ignore teardown exceptions to avoid masking test failures.
                }
            }
            _disposables.Clear();

            for (int i = _unityObjects.Count - 1; 0 <= i; i--)
            {
                UnityEngine.Object instance = _unityObjects[i];
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
            _unityObjects.Clear();
        }
    }
}

#endif
