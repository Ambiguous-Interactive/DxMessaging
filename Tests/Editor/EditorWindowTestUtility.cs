#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using DxMessaging.Editor;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.TestTools;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    internal sealed class DxMessagingTestHostWindow : EditorWindow
    {
        internal const string TitleText = "DxMessaging Test Host";
    }

    internal static class EditorWindowTestUtility
    {
        private static readonly List<EditorWindow> CreatedWindows = new();

        /// <summary>
        /// Sub-pixel slack for layout comparisons: Yoga rounds to the panel's pixel grid.
        /// </summary>
        internal const float LayoutTolerance = 0.5f;

        /// <summary>
        /// Unity's largest representable length. An element reporting it was laid out with no
        /// bound rather than measured.
        /// </summary>
        private const float UnboundedLayoutSize = 8388608f;

        /// <summary>
        /// How many layout passes <see cref="SettleLayout"/> will run before giving up on the
        /// tree settling. Real windows settle in two or three.
        /// </summary>
        private const int MaxLayoutPasses = 8;

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
            SuppressHeadlessWindowRenderErrors();
            window.Show();
            window.hideFlags = HideFlags.HideAndDontSave;
        }

        /// <summary>
        /// Shows a tracked capture host without a dock tab. A normal <see cref="EditorWindow.Show"/>
        /// paints the test window's tab into the same panel render target as its root visual tree
        /// on macOS, so an offscreen readback cannot separate that native chrome from the package
        /// surface. Popup mode still creates the attached panel the renderer needs but adds no tab.
        /// </summary>
        internal static void ShowPopupWindow(EditorWindow window)
        {
            if (window == null)
            {
                return;
            }

            window.hideFlags = HideFlags.HideAndDontSave;
            SuppressHeadlessWindowRenderErrors();
            window.ShowPopup();
            window.hideFlags = HideFlags.HideAndDontSave;
        }

        /// <summary>
        /// Headless CI runs Unity with -nographics, where showing a window and repainting
        /// the inspector (including while destroying editors/objects during teardown) logs
        /// benign "No graphic device is available to initialize the view. / show the window."
        /// errors. NUnit fails a test on any unexpected error log, and Unity resets
        /// <see cref="LogAssert.ignoreFailingMessages"/> per phase, so tests that show windows
        /// must re-assert tolerance in every phase where these errors can fire (the test body
        /// via <see cref="ShowWindow"/>, and teardown for inspector-editor destruction). Only
        /// active when no graphics device is present, so runs with a real GPU keep full log
        /// strictness; Unity's per-test LogScope clears the flag for the next test.
        /// </summary>
        internal static void SuppressHeadlessWindowRenderErrors()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                LogAssert.ignoreFailingMessages = true;
            }
        }

        internal static void CloseWindow(EditorWindow window)
        {
            if (window == null)
            {
                return;
            }

            window.rootVisualElement.Clear();
            CreatedWindows.Remove(window);

            // A window that was created but never shown has no host view, and
            // EditorWindow.Close() dereferences that parent unconditionally. Destroying the
            // instance is the whole of its teardown. Some tests inspect an unattached root tree,
            // so closing one has to be safe rather than a NullReferenceException in teardown.
            if (ReadMember(window, "m_Parent") == null)
            {
                Object.DestroyImmediate(window);
                return;
            }

            window.Close();
        }

        internal static void CloseTrackedWindows(List<EditorWindow> windows)
        {
            CloseWindows(windows);
            CloseWindows(CreatedWindows);
            // Intentionally do NOT reset LogAssert.ignoreFailingMessages here. Fixtures run
            // more teardown after this call (e.g. DestroyImmediate of inspector editors and
            // scene objects) which, in -nographics, re-emits the benign "No graphic device"
            // error; resetting mid-teardown would let those slip through as unexpected logs.
            // Unity gives each test a fresh LogScope (ignoreFailingMessages defaults back to
            // false next test), so the nographics tolerance ShowWindow enabled cannot leak.
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

        /// <summary>
        /// Runs the panel's layout until it stops changing, so a test reads the geometry a reader
        /// settles on rather than an intermediate frame.
        ///
        /// One pass is never enough. Realizing content changes what elements measure, text height
        /// is only final once its width is, and a
        /// <see cref="DxMessagingEditorTheme.ApplyContentSizedWrap"/> container asks for its height
        /// during a pass and receives it in the next one. Unity settles this over frames; a test
        /// has to ask for the frames.
        /// </summary>
        internal static void SettleLayout(EditorWindow window)
        {
            if (window == null)
            {
                return;
            }

            string previous = null;
            for (int pass = 0; pass < MaxLayoutPasses; pass++)
            {
                EditorSurfaceCapture.InvokeInheritedPanelMethod(
                    window.rootVisualElement.panel,
                    "ValidateLayout",
                    Array.Empty<object>()
                );

                string current = DescribeLayout(window.rootVisualElement);
                if (current == previous)
                {
                    return;
                }

                previous = current;
            }
        }

        private static string DescribeLayout(VisualElement root)
        {
            StringBuilder description = new();
            foreach (VisualElement element in root.Query<VisualElement>().ToList())
            {
                Rect layout = element.layout;
                description
                    .Append(layout.x)
                    .Append(',')
                    .Append(layout.y)
                    .Append(',')
                    .Append(layout.width)
                    .Append(',')
                    .Append(layout.height)
                    .Append(';');
            }

            return description.ToString();
        }

        /// <summary>
        /// Asserts every wrapping container under <paramref name="root"/> is tall enough for the
        /// lines its children wrap onto.
        ///
        /// Unity 2021.3 does not grow a wrapping container to fit those lines (issues #435 and
        /// #440), so the extra lines draw outside the container, on top of whatever the window
        /// paints beneath. This assertion is what reports that on the 2021.3 leg; on newer editors
        /// it holds without the fix, because they size the container correctly.
        /// </summary>
        internal static void AssertWrappingContainersContainTheirChildren(
            VisualElement root,
            string context
        )
        {
            Assert.That(root, Is.Not.Null, $"{context} must render a root element.");

            List<VisualElement> wrapping = new();
            foreach (VisualElement element in root.Query<VisualElement>().ToList())
            {
                if (element.resolvedStyle.flexWrap != Wrap.Wrap || element.childCount == 0)
                {
                    continue;
                }

                // An element laid out with no bound reports Unity's largest length rather than a
                // measurement. A box eight million pixels tall is not painting over anything a
                // reader can see, and asserting on it compares two sentinels.
                if (
                    float.IsNaN(element.resolvedStyle.height)
                    || element.resolvedStyle.height >= UnboundedLayoutSize
                )
                {
                    continue;
                }

                wrapping.Add(element);
            }

            Assert.That(
                wrapping,
                Is.Not.Empty,
                $"{context} renders no wrapping container, so this assertion would pass "
                    + "without checking anything. Point it at a surface that wraps."
            );

            foreach (VisualElement element in wrapping)
            {
                Assert.That(
                    DxMessagingEditorTheme.MeasureWrappedContentHeight(element),
                    Is.LessThanOrEqualTo(element.resolvedStyle.height + LayoutTolerance),
                    $"{context}: wrapping container '{DescribeElement(element)}' is shorter than "
                        + "the lines its children wrap onto, so those lines draw outside it and "
                        + "over whatever the window paints beneath."
                );
            }
        }

        private static string DescribeElement(VisualElement element)
        {
            if (!string.IsNullOrEmpty(element.name))
            {
                return element.name;
            }

            foreach (string className in element.GetClasses())
            {
                return "." + className;
            }

            return element.GetType().Name;
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
