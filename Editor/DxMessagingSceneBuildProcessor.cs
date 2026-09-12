namespace DxMessaging.Editor
{
#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
    using System.Collections.Generic;
    using DxMessaging.Unity;
    using UnityEditor.Build;
    using UnityEditor.Build.Reporting;
    using UnityEngine;
    using UnityEngine.SceneManagement;

    /// <summary>
    /// Ensures MessagingComponent instances do not carry runtime registrations into player builds.
    /// </summary>
    internal sealed class DxMessagingSceneBuildProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => int.MaxValue;

        /// <summary>
        /// Strips runtime-only messaging state from the scene before it is serialized into the build.
        /// </summary>
        /// <param name="scene">Scene currently being processed.</param>
        /// <param name="report">Build report associated with the scene export.</param>
        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            MessagingComponent[] components = FindMessagingComponents(scene);
            if (components == null || components.Length == 0)
            {
                return;
            }

            int cleared = 0;
            for (int i = 0; i < components.Length; ++i)
            {
                MessagingComponent component = components[i];
                if (component != null && component.EditorResetRuntimeState())
                {
                    cleared++;
                }
            }

            if (0 < cleared && report != null)
            {
                Debug.Log(
                    $"[DxMessaging] Cleared {cleared} MessagingComponent instance(s) in scene '{scene.path}' prior to build."
                );
            }
        }

        private static MessagingComponent[] FindMessagingComponents(Scene scene)
        {
            List<MessagingComponent> buffer = new List<MessagingComponent>();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; ++i)
            {
                GameObject root = roots[i];
                if (root == null)
                {
                    continue;
                }

                MessagingComponent[] components = root.GetComponentsInChildren<MessagingComponent>(
                    includeInactive: true
                );
                if (components == null || components.Length == 0)
                {
                    continue;
                }

                buffer.AddRange(components);
            }

            return buffer.ToArray();
        }
    }
#endif
}
