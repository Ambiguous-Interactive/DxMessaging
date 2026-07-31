#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using DxMessaging.Editor;
    using DxMessaging.Editor.CustomEditors;
    using DxMessaging.Editor.Settings;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Pins the documentation capture path. These tests are the retained, repeatable half of
    /// PLAN.md WS-7.3: they prove the mechanism renders real shipped views into a 24-bit PNG
    /// without touching the desktop, without leaking editor state, and without emitting console
    /// diagnostics. Choosing the final Personal/light artwork stays a human review step.
    /// </summary>
    [TestFixture]
    public sealed class EditorSurfaceCaptureTests
    {
        // The canvas is deliberately larger than the surface. The host window draws a tab
        // strip at the top of its panel, so a surface laid out at the canvas size would be
        // pushed partly off the bottom and the crop would come back short.
        private const int CanvasWidth = 960;
        private const int CanvasHeight = 600;
        private const int SurfaceWidth = 720;
        private const int SurfaceHeight = 240;

        private readonly List<Object> _createdObjects = new();
        private readonly List<string> _createdFiles = new();

        [SetUp]
        public void SetUp()
        {
            if (!EditorSurfaceCapture.IsSupported)
            {
                Assert.Ignore(
                    "Offscreen capture needs a graphics device; this editor runs with -nographics."
                );
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string path in _createdFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            _createdFiles.Clear();

            foreach (Object instance in _createdObjects)
            {
                if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }
            }
            _createdObjects.Clear();
        }

        [Test]
        public void CaptureWritesTruecolorPngWithoutAlpha()
        {
            EditorSurfaceCaptureResult result = Capture(
                CreateOpaqueProbe(),
                nameof(CaptureWritesTruecolorPngWithoutAlpha)
            );

            Assert.That(
                result.PngColorType,
                Is.EqualTo(EditorSurfaceCapture.PngTruecolorWithoutAlpha)
            );
            Assert.That(File.Exists(result.OutputPath), Is.True, result.ToString());
            Assert.That(result.ByteCount, Is.GreaterThan(0), result.ToString());
            Assert.That(
                result.Width,
                Is.EqualTo(SurfaceWidth),
                "The written image is cropped to the surface, not to the canvas."
            );
            Assert.That(result.Height, Is.EqualTo(SurfaceHeight), result.ToString());

            byte[] written = File.ReadAllBytes(result.OutputPath);
            Assert.That(
                written.Length,
                Is.EqualTo(result.ByteCount),
                "The reported byte count must describe the file that was actually written."
            );
        }

        [Test]
        public void CaptureRestoresRenderStateAndLeavesNoWindowBehind()
        {
            RenderTexture sentinel = new(64, 64, 0);
            _createdObjects.Add(sentinel);
            RenderTexture previousTarget = RenderTexture.active;
            bool previousSrgbWrite = GL.sRGBWrite;
            int windowsBefore = Resources.FindObjectsOfTypeAll<EditorWindow>().Length;
            try
            {
                RenderTexture.active = sentinel;
                GL.sRGBWrite = true;

                Capture(
                    CreateOpaqueProbe(),
                    nameof(CaptureRestoresRenderStateAndLeavesNoWindowBehind)
                );

                Assert.That(
                    RenderTexture.active,
                    Is.SameAs(sentinel),
                    "Capture must restore the active render target it found."
                );
                Assert.That(
                    GL.sRGBWrite,
                    Is.True,
                    "Capture must restore GL.sRGBWrite; leaving it off recolors every later render."
                );
            }
            finally
            {
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = previousSrgbWrite;
            }

            Assert.That(
                Resources.FindObjectsOfTypeAll<EditorWindow>().Length,
                Is.EqualTo(windowsBefore),
                "Capture must close its hidden host window."
            );
        }

        [Test]
        public void CaptureRestoresRenderStateWhenTheSurfaceThrows()
        {
            RenderTexture sentinel = new(64, 64, 0);
            _createdObjects.Add(sentinel);
            RenderTexture previousTarget = RenderTexture.active;
            bool previousSrgbWrite = GL.sRGBWrite;
            int windowsBefore = Resources.FindObjectsOfTypeAll<EditorWindow>().Length;

            // The failure has to happen INSIDE the capture's try, after it has taken over the
            // render target and GL.sRGBWrite and created its host window. An argument-validation
            // failure would return before any of that and this test would pass even if the whole
            // finally block were deleted. An oversized surface fails at the crop step, which is
            // past every piece of state the finally is responsible for putting back.
            VisualElement oversized = CreateOpaqueProbe();
            oversized.style.width = CanvasWidth + 1;
            try
            {
                RenderTexture.active = sentinel;
                GL.sRGBWrite = true;

                Assert.Throws<InvalidOperationException>(() =>
                    EditorSurfaceCapture.Capture(
                        oversized,
                        CanvasWidth,
                        CanvasHeight,
                        ResolveOutputPath(nameof(CaptureRestoresRenderStateWhenTheSurfaceThrows))
                    )
                );

                Assert.That(RenderTexture.active, Is.SameAs(sentinel));
                Assert.That(GL.sRGBWrite, Is.True);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = previousSrgbWrite;
            }

            Assert.That(
                Resources.FindObjectsOfTypeAll<EditorWindow>().Length,
                Is.EqualTo(windowsBefore),
                "A failed capture must still close its hidden host window."
            );
        }

        [Test]
        public void CaptureRendersTheInspectorWarningPanel()
        {
            EditorSurfaceCaptureResult result = Capture(
                CreateInspectorWarningPanel(),
                nameof(CaptureRendersTheInspectorWarningPanel)
            );

            // A cleared target has exactly one distinct color. The warning panel carries a
            // background, an amber border, a title, a body, a method list, and two buttons, so a
            // frame that really rendered cannot be flat.
            Assert.That(
                result.DistinctColorCount,
                Is.GreaterThan(1),
                $"The captured frame is a single flat color, so nothing rendered: {result}"
            );
        }

        [Test]
        public void CaptureRendersAPackageOwnedEditorWindowSurface()
        {
            EditorSurfaceCaptureResult result = Capture(
                CreateEmptyStateSurface(),
                nameof(CaptureRendersAPackageOwnedEditorWindowSurface)
            );

            Assert.That(
                result.DistinctColorCount,
                Is.GreaterThan(1),
                $"The captured frame is a single flat color, so nothing rendered: {result}"
            );
        }

        [Test]
        public void CaptureRecordsTheSkinAndUnityVersionItWasTakenUnder()
        {
            EditorSurfaceCaptureResult result = Capture(
                CreateOpaqueProbe(),
                nameof(CaptureRecordsTheSkinAndUnityVersionItWasTakenUnder)
            );

            // The manifest bans switching skins to reach Personal/light, so the capture reports
            // the skin instead of changing it and the reviewer rejects a Pro/dark artifact.
            Assert.That(result.IsProSkin, Is.EqualTo(EditorGUIUtility.isProSkin));
            Assert.That(result.UnityVersion, Is.EqualTo(Application.unityVersion));
        }

        [Test]
        public void CaptureRefusesASurfaceLargerThanTheCanvas()
        {
            VisualElement oversized = CreateOpaqueProbe();
            oversized.style.width = CanvasWidth + 1;

            // Clamping into the canvas would write a silently clipped image, which is the exact
            // defect the screenshot manifest asks reviewers to catch.
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
                EditorSurfaceCapture.Capture(
                    oversized,
                    CanvasWidth,
                    CanvasHeight,
                    ResolveOutputPath(nameof(CaptureRefusesASurfaceLargerThanTheCanvas))
                )
            );
            Assert.That(failure.Message, Does.Contain("does not fit"));
            Assert.That(
                File.Exists(ResolveOutputPath(nameof(CaptureRefusesASurfaceLargerThanTheCanvas))),
                Is.False,
                "A refused capture must not leave a partial image behind."
            );
        }

        [Test]
        public void CaptureRejectsAnEmptyOutputPath()
        {
            Assert.Throws<System.ArgumentException>(() =>
                EditorSurfaceCapture.Capture(CreateOpaqueProbe(), CanvasWidth, CanvasHeight, "   ")
            );
        }

        [Test]
        public void CaptureRejectsAMissingSurface()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
                EditorSurfaceCapture.Capture(
                    null,
                    CanvasWidth,
                    CanvasHeight,
                    ResolveOutputPath(nameof(CaptureRejectsAMissingSurface))
                )
            );
        }

        private EditorSurfaceCaptureResult Capture(VisualElement content, string name)
        {
            string path = ResolveOutputPath(name);
            _createdFiles.Add(path);
            return EditorSurfaceCapture.Capture(content, CanvasWidth, CanvasHeight, path);
        }

        private static string ResolveOutputPath(string name)
        {
            return Path.Combine(
                "Packages",
                "com.wallstop-studios.dxmessaging",
                ".artifacts",
                "unity-mcp",
                $"capture-{name}.png"
            );
        }

        private static VisualElement CreateOpaqueProbe()
        {
            VisualElement probe = new();
            DxMessagingEditorTheme.Apply(probe);
            probe.style.width = SurfaceWidth;
            probe.style.height = SurfaceHeight;
            probe.style.backgroundColor = DxMessagingEditorPalette.Amber;
            return probe;
        }

        private VisualElement CreateEmptyStateSurface()
        {
            VisualElement surface = DxMessagingEditorTheme.CreateEmptyState(
                "No emissions recorded",
                "Enter play mode with diagnostics enabled to populate the Message Monitor."
            );
            surface.style.width = SurfaceWidth;
            surface.style.height = SurfaceHeight;
            return surface;
        }

        private VisualElement CreateInspectorWarningPanel()
        {
            GameObject host = new(nameof(CreateInspectorWarningPanel));
            _createdObjects.Add(host);
            MessageAwareComponent component =
                host.AddComponent<EmptyMessageAwareComponentForCaptureTest>();

            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            settings.hideFlags = HideFlags.HideAndDontSave;
            _createdObjects.Add(settings);

            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForHarvesterUnavailable(
                    component,
                    settings,
                    component.GetType().FullName,
                    isFreshThisSession: true
                );

            VisualElement panel = MessageAwareComponentInspectorView.Create(
                state,
                MessageAwareComponentInspectorViewActions.None
            );
            panel.style.width = SurfaceWidth;
            return panel;
        }
    }

    internal sealed class EmptyMessageAwareComponentForCaptureTest : MessageAwareComponent { }
}
#endif
