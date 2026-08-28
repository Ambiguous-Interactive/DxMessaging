#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.IO;
    using System.Reflection;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.Rendering;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Renders a package-owned editor surface into an offscreen render target and encodes it as
    /// a 24-bit PNG, without reading the desktop.
    ///
    /// The documentation screenshot manifest bans Unity's internal screen-pixel reader, native
    /// window capture, and programmatic skin switching, because all three read whatever the host
    /// desktop happens to be showing. This helper never looks outside its own render target: it
    /// hosts the real shipped view in a hidden window, settles that window's panel layout, drives
    /// one repaint, and reads back only the temporary target it created. The blocked
    /// identifiers are listed in scripts/__tests__/design-system-dumps.test.js, which fails on
    /// any tracked source that names one.
    ///
    /// Two details are load-bearing and were established by experiment (see
    /// docs/images/inspector-overlay/README.md):
    ///
    /// - The panel's <c>ValidateLayout</c> and <c>Render</c> are inherited, so they must be
    ///   reflected with instance, public, and non-public binding flags and WITHOUT
    ///   <see cref="BindingFlags.DeclaredOnly"/>; declaring-type-only lookup finds nothing.
    /// - In a linear-color project, a linear render target with <c>GL.sRGBWrite</c> disabled is
    ///   what matches the colors the real panel shows.
    /// </summary>
    internal static class EditorSurfaceCapture
    {
        /// <summary>PNG IHDR color type 2: truecolor, no alpha channel. The manifest requires it.</summary>
        internal const byte PngTruecolorWithoutAlpha = 2;

        /// <summary>
        /// Byte offset of the IHDR color-type field: 8-byte signature + 4-byte length +
        /// 4-byte "IHDR" + 4-byte width + 4-byte height + 1-byte bit depth.
        /// </summary>
        private const int PngColorTypeOffset = 25;

        private const BindingFlags InheritedInstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Offscreen rendering needs a real graphics device. CI runs Unity with
        /// <c>-nographics</c>, so capture tests skip there rather than assert against a device
        /// that cannot rasterize anything.
        /// </summary>
        internal static bool IsSupported =>
            SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>
        /// Renders <paramref name="content"/> onto a <paramref name="canvasWidth"/> x
        /// <paramref name="canvasHeight"/> offscreen canvas and writes a 24-bit PNG of the
        /// surface's own laid-out rect to <paramref name="outputPath"/>. The canvas only has to
        /// be large enough to hold the surface; the written image is cropped to the surface, so
        /// its dimensions come from the surface's layout rather than from these arguments.
        ///
        /// Every global the render touches -- the active render target and <c>GL.sRGBWrite</c> --
        /// is restored, and every object it creates is destroyed, including on the failure path.
        /// </summary>
        internal static EditorSurfaceCaptureResult Capture(
            VisualElement content,
            int canvasWidth,
            int canvasHeight,
            string outputPath
        )
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            if (canvasWidth <= 0 || canvasHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(canvasWidth),
                    $"Capture canvas must be positive, got {canvasWidth}x{canvasHeight}."
                );
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Capture needs an output path.", nameof(outputPath));
            }

            if (!IsSupported)
            {
                throw new InvalidOperationException(
                    "EditorSurfaceCapture needs a graphics device; this editor is running with -nographics."
                );
            }

            RenderTexture previousTarget = RenderTexture.active;
            bool previousSrgbWrite = GL.sRGBWrite;
            EditorWindow host = null;
            RenderTexture target = null;
            Texture2D readback = null;
            try
            {
                host = EditorWindowTestUtility.CreateWindow();
                host.minSize = new Vector2(canvasWidth, canvasHeight);
                host.position = new Rect(0f, 0f, canvasWidth, canvasHeight);
                // A window only gets a panel once it is shown, and a panel is what
                // ValidateLayout and Render operate on. Popup mode avoids painting a dock tab
                // into that panel on macOS. Showing it does not make the capture read the
                // desktop: the pixels still come from the render target below, never from the
                // screen.
                EditorWindowTestUtility.ShowPopupWindow(host);

                VisualElement root = host.rootVisualElement;
                root.style.width = canvasWidth;
                root.style.height = canvasHeight;
                root.Add(content);

                IPanel panel = root.panel;
                if (panel == null)
                {
                    throw new InvalidOperationException(
                        "The capture host window produced no panel to render."
                    );
                }

                target = new RenderTexture(
                    canvasWidth,
                    canvasHeight,
                    24,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear
                );
                if (!target.Create())
                {
                    throw new InvalidOperationException(
                        $"Could not create a {canvasWidth}x{canvasHeight} capture canvas."
                    );
                }

                GL.sRGBWrite = false;
                RenderTexture.active = target;
                GL.Clear(true, true, Color.clear);

                // Three steps, in this order. Settling layout resolves deferred text, scroll,
                // and wrapping geometry across as many passes as the tree needs. Repaint walks
                // the settled tree and records the draw commands, and Render flushes those
                // commands to the active target. Nested ScrollViews realize their content during
                // the first repaint, and newly introduced glyphs can extend the dynamic font
                // atlas during the second. A third repaint/render cycle draws both settled sets.
                // Stopping earlier can yield a valid PNG with blank scroll bodies or partially
                // missing labels, which is why the tests inspect the rendered content too.
                EditorWindowTestUtility.SettleLayout(host);
                for (int repaintPass = 0; repaintPass < 3; repaintPass++)
                {
                    InvokeInheritedPanelMethod(
                        panel,
                        "Repaint",
                        new object[] { new Event { type = EventType.Repaint } }
                    );
                    InvokeInheritedPanelMethod(panel, "Render", Array.Empty<object>());
                }

                // Read back only the surface, not the whole canvas. Cropping to the surface's
                // own laid-out rect gives the manifest its tight frame: the padding in the image
                // comes from the surface's styling, not from slack in the canvas.
                RectInt crop = ResolveCropRect(content, canvasWidth, canvasHeight);
                readback = new Texture2D(crop.width, crop.height, TextureFormat.RGB24, false, true);
                readback.ReadPixels(new Rect(crop.x, crop.y, crop.width, crop.height), 0, 0, false);
                readback.Apply(false, false);

                byte[] png = readback.EncodeToPNG();
                if (png == null || png.Length <= PngColorTypeOffset)
                {
                    throw new InvalidOperationException("PNG encoding produced no usable bytes.");
                }

                byte colorType = png[PngColorTypeOffset];
                if (colorType != PngTruecolorWithoutAlpha)
                {
                    throw new InvalidOperationException(
                        $"Capture produced PNG color type {colorType}; the manifest requires "
                            + $"{PngTruecolorWithoutAlpha} (truecolor without alpha)."
                    );
                }

                string directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(outputPath, png);

                return new EditorSurfaceCaptureResult(
                    outputPath,
                    crop.width,
                    crop.height,
                    png.Length,
                    colorType,
                    CountDistinctColors(readback),
                    EditorGUIUtility.isProSkin,
                    Application.unityVersion
                );
            }
            finally
            {
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = previousSrgbWrite;

                if (readback != null)
                {
                    Object.DestroyImmediate(readback);
                }

                if (target != null)
                {
                    target.Release();
                    Object.DestroyImmediate(target);
                }

                if (host != null)
                {
                    EditorWindowTestUtility.CloseWindow(host);
                }
            }
        }

        /// <summary>
        /// Translates the surface's laid-out rect into render-target coordinates. UI Toolkit
        /// measures from the top-left; a render target's rows start at the bottom, so the
        /// vertical origin is the distance from the canvas bottom to the surface's bottom edge.
        /// </summary>
        private static RectInt ResolveCropRect(
            VisualElement content,
            int canvasWidth,
            int canvasHeight
        )
        {
            // Round the EDGES, then derive the size from them. Rounding the origin and the size
            // independently lets the two drift a pixel apart -- a surface at x=10.5 w=10.5 rounds
            // to x=10 w=10, a right edge of 20 where the real one is 21 -- and a pixel lost here
            // is a pixel of the surface clipped out of a documentation image.
            Rect bounds = content.worldBound;
            int cropX = Mathf.RoundToInt(bounds.x);
            int cropY = Mathf.RoundToInt(canvasHeight - bounds.yMax);
            int cropWidth = Mathf.RoundToInt(bounds.xMax) - cropX;
            int cropHeight = Mathf.RoundToInt(canvasHeight - bounds.y) - cropY;
            if (cropWidth <= 0 || cropHeight <= 0)
            {
                throw new InvalidOperationException(
                    $"The surface laid out to {bounds.width}x{bounds.height}, so there is nothing "
                        + "to capture. Give it an explicit size or content before capturing."
                );
            }

            // Refuse a surface that does not fit rather than clamping into the canvas. Clamping
            // would return a silently clipped image, which is exactly the defect the manifest
            // tells reviewers to look for, produced by the tool that exists to avoid it.
            if (
                cropX < 0
                || cropY < 0
                || cropX + cropWidth > canvasWidth
                || cropY + cropHeight > canvasHeight
            )
            {
                throw new InvalidOperationException(
                    $"The surface laid out to {bounds}, which does not fit inside the "
                        + $"{canvasWidth}x{canvasHeight} capture canvas. Enlarge the canvas; "
                        + "cropping it here would write a silently clipped image."
                );
            }

            return new RectInt(cropX, cropY, cropWidth, cropHeight);
        }

        internal static void InvokeInheritedPanelMethod(
            IPanel panel,
            string methodName,
            object[] arguments
        )
        {
            Type panelType = panel.GetType();
            Type[] argumentTypes = new Type[arguments.Length];
            for (int index = 0; index < arguments.Length; index++)
            {
                argumentTypes[index] = arguments[index].GetType();
            }

            MethodInfo method = panelType.GetMethod(
                methodName,
                InheritedInstanceMembers,
                binder: null,
                types: argumentTypes,
                modifiers: null
            );
            if (method == null)
            {
                throw new InvalidOperationException(
                    $"Panel type {panelType.FullName} exposes no '{methodName}' method taking "
                        + $"{argumentTypes.Length} argument(s) to drive the capture."
                );
            }

            method.Invoke(panel, arguments);
        }

        /// <summary>
        /// A blank frame and a rendered frame are both valid PNGs, so callers need a cheap way to
        /// prove the panel actually drew. Distinct-color count is that proof: a cleared target has
        /// exactly one.
        /// </summary>
        private static int CountDistinctColors(Texture2D texture)
        {
            Color32[] pixels = texture.GetPixels32();
            System.Collections.Generic.HashSet<int> distinct = new();
            foreach (Color32 pixel in pixels)
            {
                distinct.Add((pixel.r << 16) | (pixel.g << 8) | pixel.b);
            }

            return distinct.Count;
        }
    }

    internal readonly struct EditorSurfaceCaptureResult
    {
        internal EditorSurfaceCaptureResult(
            string outputPath,
            int width,
            int height,
            int byteCount,
            byte pngColorType,
            int distinctColorCount,
            bool isProSkin,
            string unityVersion
        )
        {
            OutputPath = outputPath;
            Width = width;
            Height = height;
            ByteCount = byteCount;
            PngColorType = pngColorType;
            DistinctColorCount = distinctColorCount;
            IsProSkin = isProSkin;
            UnityVersion = unityVersion;
        }

        internal string OutputPath { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal int ByteCount { get; }

        internal byte PngColorType { get; }

        internal int DistinctColorCount { get; }

        /// <summary>
        /// Records the host skin as artifact metadata without changing the developer's preference.
        /// </summary>
        internal bool IsProSkin { get; }

        internal string UnityVersion { get; }

        public override string ToString()
        {
            return $"{OutputPath} {Width}x{Height} bytes={ByteCount} pngColorType={PngColorType} "
                + $"distinctColors={DistinctColorCount} isProSkin={IsProSkin} unity={UnityVersion}";
        }
    }
}
#endif
