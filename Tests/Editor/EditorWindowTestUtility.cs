#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.TestTools;
    using Object = UnityEngine.Object;

    internal sealed class DxMessagingTestHostWindow : EditorWindow
    {
        internal const string TitleText = "DxMessaging Test Host";
    }

    internal static class EditorWindowTestUtility
    {
        private static readonly List<EditorWindow> CreatedWindows = new();

        internal static DxMessagingTestHostWindow CreateWindow()
        {
            DxMessagingTestHostWindow window =
                ScriptableObject.CreateInstance<DxMessagingTestHostWindow>();
            window.titleContent = new GUIContent(DxMessagingTestHostWindow.TitleText);
            window.hideFlags = HideFlags.HideAndDontSave;
            CreatedWindows.Add(window);
            return window;
        }

        internal static void ShowWindow(EditorWindow window)
        {
            if (window == null)
            {
                return;
            }

            window.hideFlags = HideFlags.HideAndDontSave;
            window.Show();
            window.hideFlags = HideFlags.HideAndDontSave;
        }

        internal static void CloseWindow(EditorWindow window)
        {
            if (window == null)
            {
                return;
            }

            window.rootVisualElement.Clear();
            CreatedWindows.Remove(window);
            window.Close();
        }

        internal static void CloseTrackedWindows(List<EditorWindow> windows)
        {
            CloseWindows(windows);
            CloseWindows(CreatedWindows);
        }

        private static void CloseWindows(List<EditorWindow> windows)
        {
            if (windows == null)
            {
                return;
            }

            EditorWindow[] snapshot = windows.ToArray();
            foreach (EditorWindow window in snapshot)
            {
                CloseWindow(window);
            }

            windows.Clear();
        }

        internal static void CloseLeakedEditorWindows()
        {
            IgnoreUnityInvalidGcHandleAsserts(() =>
            {
                CloseLeakedTestHostWindows();
                CloseLeakedGenericEditorWindowContainers();
            });
        }

        internal static void CloseLeakedTestHostWindows()
        {
            foreach (
                EditorWindow window in Resources.FindObjectsOfTypeAll<DxMessagingTestHostWindow>()
            )
            {
                CloseWindow(window);
            }
        }

        private static void CloseLeakedGenericEditorWindowContainers()
        {
            System.Type dockAreaType = FindUnityEditorType("UnityEditor.DockArea");
            System.Type containerWindowType = FindUnityEditorType("UnityEditor.ContainerWindow");
            if (dockAreaType == null || containerWindowType == null)
            {
                return;
            }

            List<Object> containers = new();
            foreach (Object dockArea in Resources.FindObjectsOfTypeAll(dockAreaType))
            {
                object viewObject = ReadMember(dockArea, "actualView");
                if (
                    viewObject == null
                    || viewObject.GetType().FullName != "UnityEditor.EditorWindow"
                    || dockArea.name != "EditorWindow"
                )
                {
                    continue;
                }

                Object container = ReadMember(dockArea, "window") as Object;
                if (
                    container == null
                    || container.GetType().FullName != "UnityEditor.ContainerWindow"
                    || containers.Contains(container)
                )
                {
                    continue;
                }

                containers.Add(container);
            }

            System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.MethodInfo closeMethod = containerWindowType.GetMethod(
                "Close",
                flags,
                binder: null,
                types: System.Type.EmptyTypes,
                modifiers: null
            );
            if (closeMethod == null)
            {
                return;
            }

            foreach (Object container in containers)
            {
                try
                {
                    closeMethod.Invoke(container, parameters: null);
                }
                catch
                {
                    // Best-effort cleanup for Unity layout orphans from interrupted tests.
                }
            }
        }

        internal static void IgnoreUnityInvalidGcHandleAsserts(Action action)
        {
            if (action == null)
            {
                return;
            }

            bool previous = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                action();
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previous;
            }
        }

        private static System.Type FindUnityEditorType(string fullName)
        {
            foreach (
                System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies()
            )
            {
                System.Type type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static object ReadMember(object instance, string memberName)
        {
            System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;
            for (System.Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                System.Reflection.PropertyInfo property = type.GetProperty(memberName, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        return property.GetValue(instance, index: null);
                    }
                    catch
                    {
                        return null;
                    }
                }

                System.Reflection.FieldInfo field = type.GetField(memberName, flags);
                if (field != null)
                {
                    try
                    {
                        return field.GetValue(instance);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }

            return null;
        }
    }
}
#endif
