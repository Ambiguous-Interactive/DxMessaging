#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using UnityEditor;
    using UnityEditor.SceneManagement;
    using UnityEngine;
    using UnityEngine.SceneManagement;

    internal sealed class OwnedEditModeScene : IDisposable
    {
        private readonly Scene _originalActiveScene;
        private readonly bool _isPreviewScene;
        private Scene _scene;
        private bool _disposed;

        private OwnedEditModeScene(Scene scene, bool isPreviewScene, Scene originalActiveScene)
        {
            _scene = scene;
            _isPreviewScene = isPreviewScene;
            _originalActiveScene = originalActiveScene;
        }

        internal Scene Scene
        {
            get
            {
                ThrowIfDisposed();
                return _scene;
            }
        }

        internal static OwnedEditModeScene OpenAuthored(string scenePath)
        {
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                throw new ArgumentException(
                    "An authored scene path is required.",
                    nameof(scenePath)
                );
            }

            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene loaded = SceneManager.GetSceneAt(index);
                if (
                    loaded.isLoaded
                    && string.Equals(loaded.path, scenePath, StringComparison.OrdinalIgnoreCase)
                )
                {
                    throw new InvalidOperationException(
                        $"Cannot claim authored scene '{scenePath}' because it was already loaded."
                    );
                }
            }

            Scene originalActiveScene = SceneManager.GetActiveScene();
            Scene opened = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            return new OwnedEditModeScene(
                opened,
                isPreviewScene: false,
                originalActiveScene: originalActiveScene
            );
        }

        internal static OwnedEditModeScene CreatePreview()
        {
            return new OwnedEditModeScene(
                EditorSceneManager.NewPreviewScene(),
                isPreviewScene: true,
                originalActiveScene: SceneManager.GetActiveScene()
            );
        }

        internal void Activate()
        {
            ThrowIfDisposed();
            if (_isPreviewScene)
            {
                throw new InvalidOperationException(
                    "A preview scene cannot become the active scene."
                );
            }

            if (!SceneManager.SetActiveScene(_scene))
            {
                throw new InvalidOperationException(
                    $"Unity refused to activate fixture-owned scene '{_scene.path}'."
                );
            }
        }

        internal GameObject CreateGameObject(string name, params Type[] componentTypes)
        {
            ThrowIfDisposed();
            GameObject gameObject = EditorUtility.CreateGameObjectWithHideFlags(
                name,
                HideFlags.HideAndDontSave
            );
            SceneManager.MoveGameObjectToScene(gameObject, _scene);
            gameObject.hideFlags = HideFlags.None;

            if (componentTypes != null)
            {
                foreach (Type componentType in componentTypes)
                {
                    if (componentType == null)
                    {
                        throw new ArgumentException(
                            "Scene-resident component types cannot contain null.",
                            nameof(componentTypes)
                        );
                    }
                    gameObject.AddComponent(componentType);
                }
            }

            return gameObject;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            List<Exception> failures = new();
            try
            {
                if (
                    _originalActiveScene.IsValid()
                    && _originalActiveScene.isLoaded
                    && SceneManager.GetActiveScene().handle != _originalActiveScene.handle
                )
                {
                    if (!SceneManager.SetActiveScene(_originalActiveScene))
                    {
                        throw new InvalidOperationException(
                            $"Unity refused to restore active scene '{_originalActiveScene.path}'."
                        );
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                if (_scene.IsValid())
                {
                    if (_isPreviewScene)
                    {
                        if (!EditorSceneManager.ClosePreviewScene(_scene))
                        {
                            throw new InvalidOperationException(
                                "Unity refused to close the fixture-owned preview scene."
                            );
                        }
                    }
                    else if (_scene.isLoaded)
                    {
                        if (!EditorSceneManager.CloseScene(_scene, removeScene: true))
                        {
                            throw new InvalidOperationException(
                                $"Unity refused to close fixture-owned scene '{_scene.path}'."
                            );
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                if (!_scene.IsValid() || !_scene.isLoaded)
                {
                    _scene = default;
                }
            }

            if (failures.Count == 1)
            {
                _disposed = false;
                throw failures[0];
            }
            if (failures.Count > 1)
            {
                _disposed = false;
                throw new AggregateException("Fixture-owned scene cleanup failed.", failures);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OwnedEditModeScene));
            }
        }
    }
}
#endif
