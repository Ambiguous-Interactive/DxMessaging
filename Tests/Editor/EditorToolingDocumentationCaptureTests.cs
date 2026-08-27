#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Analyzers;
    using DxMessaging.Editor.CustomEditors;
    using DxMessaging.Editor.Settings;
    using DxMessaging.Editor.Windows;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Generates the published documentation artwork for the package-owned Inspector, Project
    /// Settings, Message Monitor, and Flow Graph UI Toolkit surfaces. The explicit capture test
    /// is the human-invoked writer; the ordinary contract test keeps the output inventory
    /// reviewable without changing tracked files during CI.
    /// </summary>
    [TestFixture]
    public sealed class EditorToolingDocumentationCaptureTests
    {
        private const string Dxmsg006FileName = "dxmsg006-overlay.png";
        private const string Dxmsg007FileName = "dxmsg007-overlay.png";
        private const string Dxmsg009FileName = "dxmsg009-overlay.png";
        private const string Dxmsg010FileName = "dxmsg010-overlay.png";
        private const string FlowGraphFileName = "flow-graph.png";
        private const string InspectorIgnoredFileName = "inspector-ignored.png";
        private const string InspectorSubscriptionsFileName = "inspector-subscriptions.png";
        private const string MessageMonitorFileName = "message-monitor.png";
        private const string ProjectSettingsFileName = "project-settings-panel.png";

        internal const string DocumentationOutputDirectory =
            "Packages/com.wallstop-studios.dxmessaging/docs/images/inspector-overlay";

        internal static IReadOnlyList<string> CapturedFileNames { get; } =
            new[]
            {
                Dxmsg006FileName,
                Dxmsg007FileName,
                Dxmsg009FileName,
                Dxmsg010FileName,
                FlowGraphFileName,
                InspectorIgnoredFileName,
                InspectorSubscriptionsFileName,
                MessageMonitorFileName,
                ProjectSettingsFileName,
            };

        /// <remarks>
        /// Added 2026-08-27 after Unity's bundled NUnit rejected <c>Has.Count</c> for an
        /// <see cref="IReadOnlyList{T}"/>-typed property. Assert the interface's count directly
        /// so this contract runs on every supported Unity test framework.
        /// </remarks>
        [Test]
        public void CaptureInventoryNamesEveryPublishedAutomatedSurfaceExactlyOnce()
        {
            Assert.That(
                CapturedFileNames,
                Is.Unique,
                "Every generated documentation image needs one unambiguous output path."
            );
            Assert.That(
                CapturedFileNames.Count,
                Is.EqualTo(9),
                "The inventory covers four warning states plus Inspector subscriptions, "
                    + "Project Settings, Message Monitor, and Flow Graph."
            );
            foreach (string fileName in CapturedFileNames)
            {
                string imagePath = Path.Combine(DocumentationOutputDirectory, fileName);
                Assert.That(File.Exists(imagePath), Is.True, $"Missing published {imagePath}.");
                Assert.That(
                    File.Exists(imagePath + ".meta"),
                    Is.True,
                    $"Missing Unity metadata for {imagePath}."
                );
            }
        }

        [Test]
        [Explicit("Writes the reviewed documentation PNG set from the current host editor.")]
        public void CaptureAllPublishedEditorTooling()
        {
            if (!EditorSurfaceCapture.IsSupported)
            {
                Assert.Ignore(
                    "Offscreen capture needs a graphics device; this editor runs with -nographics."
                );
            }

            IReadOnlyList<EditorSurfaceCaptureResult> results = CaptureAll(
                DocumentationOutputDirectory
            );

            Assert.That(
                results.Count,
                Is.EqualTo(CapturedFileNames.Count),
                "The capture must publish every declared documentation image."
            );
            foreach (EditorSurfaceCaptureResult result in results)
            {
                Assert.That(
                    result.PngColorType,
                    Is.EqualTo(EditorSurfaceCapture.PngTruecolorWithoutAlpha),
                    result.ToString()
                );
                Assert.That(result.DistinctColorCount, Is.GreaterThan(1), result.ToString());
                Assert.That(result.ByteCount, Is.GreaterThan(0), result.ToString());
                Assert.That(File.Exists(result.OutputPath), Is.True, result.ToString());
                Assert.That(File.Exists(result.OutputPath + ".meta"), Is.True, result.ToString());
                TestContext.Progress.WriteLine(result);
            }
            for (int index = 0; index < results.Count; index++)
            {
                Assert.That(
                    Path.GetFileName(results[index].OutputPath),
                    Is.EqualTo(CapturedFileNames[index]),
                    $"Published capture {index} must match the declared catalog."
                );
            }
        }

        internal static IReadOnlyList<EditorSurfaceCaptureResult> CaptureAll(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException(
                    "Capture needs an output directory.",
                    nameof(outputDirectory)
                );
            }
            if (!EditorSurfaceCapture.IsSupported)
            {
                throw new InvalidOperationException(
                    "Editor tooling capture needs a graphics device; this editor is running with -nographics."
                );
            }

            string stagingDirectory = Path.Combine(
                "Temp",
                "DxMessagingEditorToolingCapture-" + Guid.NewGuid().ToString("N")
            );
            List<EditorSurfaceCaptureResult> stagedResults = new(CapturedFileNames.Count);
            List<Object> createdObjects = new();
            try
            {
                Directory.CreateDirectory(stagingDirectory);

                DxMessagingSettings settings =
                    ScriptableObject.CreateInstance<DxMessagingSettings>();
                settings.hideFlags = HideFlags.HideAndDontSave;
                createdObjects.Add(settings);

                GameObject host = EditorUtility.CreateGameObjectWithHideFlags(
                    "DocumentationCaptureHost",
                    HideFlags.HideAndDontSave
                );
                createdObjects.Add(host);
                DocumentationMessageAwareComponent component =
                    host.AddComponent<DocumentationMessageAwareComponent>();

                stagedResults.Add(
                    CaptureInspectorWarning(
                        component,
                        settings,
                        "DXMSG006",
                        "Awake",
                        Dxmsg006FileName,
                        stagingDirectory
                    )
                );
                stagedResults.Add(
                    CaptureInspectorWarning(
                        component,
                        settings,
                        "DXMSG007",
                        "OnEnable",
                        Dxmsg007FileName,
                        stagingDirectory
                    )
                );
                stagedResults.Add(
                    CaptureInspectorWarning(
                        component,
                        settings,
                        "DXMSG009",
                        "OnEnable",
                        Dxmsg009FileName,
                        stagingDirectory
                    )
                );
                stagedResults.Add(
                    CaptureInspectorWarning(
                        component,
                        settings,
                        "DXMSG010",
                        "OnEnable",
                        Dxmsg010FileName,
                        stagingDirectory
                    )
                );
                stagedResults.Add(CaptureFlowGraph(stagingDirectory));
                stagedResults.Add(CaptureIgnoredInspector(component, settings, stagingDirectory));
                stagedResults.Add(CaptureSubscriptions(component, stagingDirectory));
                stagedResults.Add(CaptureMessageMonitor(stagingDirectory));
                stagedResults.Add(CaptureProjectSettings(settings, stagingDirectory));

                ValidateCapturedResults(stagedResults);
                return PublishCapturedResults(stagedResults, stagingDirectory, outputDirectory);
            }
            finally
            {
                foreach (Object instance in createdObjects)
                {
                    if (instance != null)
                    {
                        Object.DestroyImmediate(instance);
                    }
                }

                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }

        private static void ValidateCapturedResults(
            IReadOnlyList<EditorSurfaceCaptureResult> results
        )
        {
            if (results.Count != CapturedFileNames.Count)
            {
                throw new InvalidOperationException(
                    $"Capture produced {results.Count} images; expected {CapturedFileNames.Count}."
                );
            }

            for (int index = 0; index < CapturedFileNames.Count; index++)
            {
                string actual = Path.GetFileName(results[index].OutputPath);
                string expected = CapturedFileNames[index];
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Capture {index} produced '{actual}'; expected '{expected}'."
                    );
                }
            }
        }

        [Test]
        public void PublishCapturedResultsReturnsPublishedPaths()
        {
            string testRoot = Path.Combine(
                "Temp",
                "DxMessagingCapturePublishSuccess-" + Guid.NewGuid().ToString("N")
            );
            string stagingDirectory = Path.Combine(testRoot, "staging");
            string outputDirectory = Path.Combine(testRoot, "output");
            try
            {
                IReadOnlyList<EditorSurfaceCaptureResult> stagedResults = CreateStagedResultSet(
                    stagingDirectory
                );

                IReadOnlyList<EditorSurfaceCaptureResult> publishedResults = PublishCapturedResults(
                    stagedResults,
                    stagingDirectory,
                    outputDirectory
                );

                for (int index = 0; index < publishedResults.Count; index++)
                {
                    string expectedPath = Path.Combine(outputDirectory, CapturedFileNames[index]);
                    Assert.That(
                        publishedResults[index].OutputPath,
                        Is.EqualTo(expectedPath),
                        $"Published result {index} must name its destination rather than staging."
                    );
                    Assert.That(
                        File.Exists(expectedPath),
                        Is.True,
                        $"Published result {index} must exist at {expectedPath}."
                    );
                }
            }
            finally
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
        }

        [Test]
        public void PublishCapturedResultsRestoresPriorSetWhenReplacementFails()
        {
            string testRoot = Path.Combine(
                "Temp",
                "DxMessagingCapturePublishRollback-" + Guid.NewGuid().ToString("N")
            );
            string stagingDirectory = Path.Combine(testRoot, "staging");
            string outputDirectory = Path.Combine(testRoot, "output");
            byte[] previousFirst = { 201 };
            try
            {
                IReadOnlyList<EditorSurfaceCaptureResult> stagedResults = CreateStagedResultSet(
                    stagingDirectory
                );
                Directory.CreateDirectory(outputDirectory);
                string firstPath = Path.Combine(outputDirectory, CapturedFileNames[0]);
                string secondPath = Path.Combine(outputDirectory, CapturedFileNames[1]);
                File.WriteAllBytes(firstPath, previousFirst);

                int publishAttempt = 0;
                Assert.Throws<IOException>(
                    () =>
                        PublishCapturedResults(
                            stagedResults,
                            stagingDirectory,
                            outputDirectory,
                            (sourcePath, destinationPath) =>
                            {
                                publishAttempt++;
                                if (publishAttempt == 3)
                                {
                                    throw new IOException("Deterministic third-copy failure.");
                                }
                                File.Copy(sourcePath, destinationPath, overwrite: true);
                            }
                        ),
                    "The injected third-copy failure must abort publishing."
                );

                Assert.That(
                    File.ReadAllBytes(firstPath),
                    Is.EqualTo(previousFirst),
                    "Rollback must restore a destination that existed before publishing."
                );
                Assert.That(
                    File.Exists(secondPath),
                    Is.False,
                    "Rollback must remove a destination created before a later copy failed."
                );
            }
            finally
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
        }

        private static IReadOnlyList<EditorSurfaceCaptureResult> PublishCapturedResults(
            IReadOnlyList<EditorSurfaceCaptureResult> stagedResults,
            string stagingDirectory,
            string outputDirectory,
            Action<string, string> publishFile = null
        )
        {
            string backupDirectory = Path.Combine(stagingDirectory, "previous-published-files");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(backupDirectory);

            HashSet<string> existingFileNames = new(StringComparer.Ordinal);
            foreach (string fileName in CapturedFileNames)
            {
                string outputPath = Path.Combine(outputDirectory, fileName);
                if (!File.Exists(outputPath))
                {
                    continue;
                }

                File.Copy(outputPath, Path.Combine(backupDirectory, fileName));
                existingFileNames.Add(fileName);
            }

            try
            {
                publishFile ??= (sourcePath, destinationPath) =>
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                foreach (EditorSurfaceCaptureResult result in stagedResults)
                {
                    string outputPath = Path.Combine(
                        outputDirectory,
                        Path.GetFileName(result.OutputPath)
                    );
                    publishFile(result.OutputPath, outputPath);
                }
            }
            catch (Exception publishException)
            {
                try
                {
                    foreach (string fileName in CapturedFileNames)
                    {
                        string outputPath = Path.Combine(outputDirectory, fileName);
                        if (existingFileNames.Contains(fileName))
                        {
                            File.Copy(
                                Path.Combine(backupDirectory, fileName),
                                outputPath,
                                overwrite: true
                            );
                        }
                        else if (File.Exists(outputPath))
                        {
                            File.Delete(outputPath);
                        }
                    }
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Capture publishing failed and its prior-file rollback also failed.",
                        publishException,
                        rollbackException
                    );
                }

                throw;
            }

            List<EditorSurfaceCaptureResult> publishedResults = new(stagedResults.Count);
            foreach (EditorSurfaceCaptureResult result in stagedResults)
            {
                publishedResults.Add(
                    new EditorSurfaceCaptureResult(
                        Path.Combine(outputDirectory, Path.GetFileName(result.OutputPath)),
                        result.Width,
                        result.Height,
                        result.ByteCount,
                        result.PngColorType,
                        result.DistinctColorCount,
                        result.IsProSkin,
                        result.UnityVersion
                    )
                );
            }
            return publishedResults;
        }

        private static IReadOnlyList<EditorSurfaceCaptureResult> CreateStagedResultSet(
            string stagingDirectory
        )
        {
            Directory.CreateDirectory(stagingDirectory);
            List<EditorSurfaceCaptureResult> results = new(CapturedFileNames.Count);
            for (int index = 0; index < CapturedFileNames.Count; index++)
            {
                string path = Path.Combine(stagingDirectory, CapturedFileNames[index]);
                File.WriteAllBytes(path, new[] { (byte)(index + 1) });
                results.Add(
                    new EditorSurfaceCaptureResult(
                        path,
                        width: 1,
                        height: 1,
                        byteCount: 1,
                        pngColorType: EditorSurfaceCapture.PngTruecolorWithoutAlpha,
                        distinctColorCount: 2,
                        isProSkin: false,
                        unityVersion: "test"
                    )
                );
            }
            return results;
        }

        private static EditorSurfaceCaptureResult CaptureInspectorWarning(
            MessageAwareComponent component,
            DxMessagingSettings settings,
            string diagnosticId,
            string methodName,
            string fileName,
            string stagingDirectory
        )
        {
            const string fullName = "Gameplay.HealthComponent";
            BaseCallReportEntry entry = new()
            {
                typeName = fullName,
                missingBaseFor = new List<string> { methodName },
                diagnosticIds = new List<string> { diagnosticId },
                filePath = "Assets/Scripts/HealthComponent.cs",
                line = 12,
            };
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                    component,
                    settings,
                    fullName,
                    entry,
                    isFreshThisSession: true
                );
            MessageAwareComponentInspectorViewActions actions = new(
                onOpenScript: _ => { },
                onIgnoreType: _ => { },
                onStopIgnoring: _ => { }
            );
            VisualElement surface = MessageAwareComponentInspectorView.Create(state, actions);
            surface.style.width = 720;
            return Capture(surface, 800, 420, fileName, stagingDirectory);
        }

        private static EditorSurfaceCaptureResult CaptureIgnoredInspector(
            MessageAwareComponent component,
            DxMessagingSettings settings,
            string stagingDirectory
        )
        {
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForIgnoredType(
                    component,
                    settings,
                    "Gameplay.HealthComponent",
                    isFreshThisSession: true
                );
            MessageAwareComponentInspectorViewActions actions = new(
                onOpenScript: _ => { },
                onIgnoreType: _ => { },
                onStopIgnoring: _ => { }
            );
            VisualElement surface = MessageAwareComponentInspectorView.Create(state, actions);
            surface.style.width = 720;
            return Capture(surface, 800, 320, InspectorIgnoredFileName, stagingDirectory);
        }

        private static EditorSurfaceCaptureResult CaptureSubscriptions(
            MessageAwareComponent component,
            string stagingDirectory
        )
        {
            MessageAwareComponentSubscriptionsState state =
                MessageAwareComponentSubscriptionsState.Capture(component);
            VisualElement surface = MessageAwareComponentSubscriptionsView.Create(state);
            surface.style.width = 720;
            return Capture(surface, 800, 420, InspectorSubscriptionsFileName, stagingDirectory);
        }

        private static EditorSurfaceCaptureResult CaptureProjectSettings(
            DxMessagingSettings settings,
            string stagingDirectory
        )
        {
            VisualElement surface = new();
            DxMessagingSettingsProvider.BuildSettingsUi(surface, new SerializedObject(settings));
            surface.style.width = 720;
            surface.style.height = 600;
            return Capture(surface, 800, 680, ProjectSettingsFileName, stagingDirectory);
        }

        private static EditorSurfaceCaptureResult CaptureMessageMonitor(string stagingDirectory)
        {
            VisualElement surface = CreateMessageMonitorSurface();
            return Capture(surface, 1200, 600, MessageMonitorFileName, stagingDirectory);
        }

        private static VisualElement CreateMessageMonitorSurface()
        {
            VisualElement surface = new();
            surface.style.width = 1120;
            surface.style.height = 520;
            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                surface,
                new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 100,
                    entries: Array.Empty<MessageMonitorEntry>()
                )
            );
            // Window roots normally flex to their dock. Pin the requested viewport or the
            // surface grows to the capture canvas instead of retaining documentation dimensions.
            surface.style.flexGrow = 0;
            surface.style.flexShrink = 0;
            return surface;
        }

        private static EditorSurfaceCaptureResult CaptureFlowGraph(string stagingDirectory)
        {
            FlowGraphComponentNode[] components =
            {
                new(
                    "component:player",
                    "Arena/Player",
                    "PlayerMessagingComponent",
                    activeInHierarchy: true,
                    listenerCount: 2,
                    registrationCount: 2,
                    callCount: 18,
                    localMessageCount: 3
                ),
                new(
                    "component:hud",
                    "UI/HUD",
                    "HudMessagingComponent",
                    activeInHierarchy: true,
                    listenerCount: 2,
                    registrationCount: 2,
                    callCount: 15,
                    localMessageCount: 0
                ),
            };
            FlowGraphMessageNode[] messages =
            {
                new(
                    "PlayerDamaged",
                    2,
                    18,
                    recentGlobalEmissionCount: 6,
                    recentTracedDeliveryCount: 6
                ),
                new(
                    "ScoreChanged",
                    2,
                    15,
                    recentGlobalEmissionCount: 5,
                    recentTracedDeliveryCount: 5
                ),
            };
            FlowGraphEdge[] edges =
            {
                new(
                    "PlayerDamaged",
                    "component:player",
                    "Arena/Player",
                    "Targeted",
                    registrationCount: 1,
                    callCount: 10,
                    recentTracedDeliveryCount: 4,
                    context: "Arena/Player"
                ),
                new(
                    "PlayerDamaged",
                    "component:hud",
                    "UI/HUD",
                    "Broadcast",
                    registrationCount: 1,
                    callCount: 8,
                    recentTracedDeliveryCount: 2
                ),
                new(
                    "ScoreChanged",
                    "component:player",
                    "Arena/Player",
                    "Untargeted",
                    registrationCount: 1,
                    callCount: 8,
                    recentTracedDeliveryCount: 3
                ),
                new(
                    "ScoreChanged",
                    "component:hud",
                    "UI/HUD",
                    "Untargeted",
                    registrationCount: 1,
                    callCount: 7,
                    recentTracedDeliveryCount: 2
                ),
            };
            FlowGraphSnapshot snapshot = new(components, messages, edges, Array.Empty<string>());
            VisualElement surface = new();
            surface.style.width = 1200;
            surface.style.height = 800;
            DxMessagingFlowGraphWindow.BuildGraphUi(surface, snapshot);
            surface.style.flexGrow = 0;
            surface.style.flexShrink = 0;
            return Capture(surface, 1280, 880, FlowGraphFileName, stagingDirectory);
        }

        private static EditorSurfaceCaptureResult Capture(
            VisualElement surface,
            int canvasWidth,
            int canvasHeight,
            string fileName,
            string stagingDirectory
        )
        {
            return EditorSurfaceCapture.Capture(
                surface,
                canvasWidth,
                canvasHeight,
                Path.Combine(stagingDirectory, fileName)
            );
        }
    }

    [AddComponentMenu("")]
    internal sealed class DocumentationMessageAwareComponent : MessageAwareComponent { }
}
#endif
