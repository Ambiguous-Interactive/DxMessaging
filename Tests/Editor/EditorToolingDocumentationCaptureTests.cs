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
        private const string Dxmsg008FileName = "dxmsg008-overlay.png";
        private const string Dxmsg009FileName = "dxmsg009-overlay.png";
        private const string Dxmsg010FileName = "dxmsg010-overlay.png";
        private const string FlowGraphFileName = "flow-graph.png";
        private const string FlowGraphComponentSelectedFileName =
            "flow-graph-component-selected.png";
        private const string FlowGraphMessageSelectedFileName = "flow-graph-message-selected.png";
        private const string FlowGraphRouteSelectedFileName = "flow-graph-route-selected.png";
        private const string InspectorSubscriptionsFileName = "inspector-subscriptions.png";
        private const string MessageMonitorFileName = "message-monitor.png";
        private const string MessageMonitorSelectedFileName = "message-monitor-selected.png";
        private const string ProjectSettingsFileName = "project-settings-panel.png";

        internal const string DocumentationOutputDirectory =
            "Packages/com.wallstop-studios.dxmessaging/docs/images/inspector-overlay";

        internal static IReadOnlyList<string> CapturedFileNames { get; } =
            new[]
            {
                Dxmsg006FileName,
                Dxmsg007FileName,
                Dxmsg008FileName,
                Dxmsg009FileName,
                Dxmsg010FileName,
                FlowGraphFileName,
                FlowGraphComponentSelectedFileName,
                FlowGraphMessageSelectedFileName,
                FlowGraphRouteSelectedFileName,
                InspectorSubscriptionsFileName,
                MessageMonitorFileName,
                MessageMonitorSelectedFileName,
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
                Is.EqualTo(13),
                "The inventory covers five analyzer states, Inspector subscriptions, "
                    + "Project Settings, two Message Monitor states, and four Flow Graph states."
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

        /// <remarks>
        /// Added 2026-08-27 after review found DXMSG007, DXMSG009, and DXMSG010 were
        /// byte-for-byte duplicates because the shipped Inspector title did not render the
        /// diagnostic ID.
        /// </remarks>
        [Test]
        public void PublishedDiagnosticCapturesAreVisuallyDistinct()
        {
            string[] diagnosticFiles =
            {
                Dxmsg006FileName,
                Dxmsg007FileName,
                Dxmsg008FileName,
                Dxmsg009FileName,
                Dxmsg010FileName,
            };
            Dictionary<string, string> encodedImages = new(StringComparer.Ordinal);
            foreach (string fileName in diagnosticFiles)
            {
                string path = Path.Combine(DocumentationOutputDirectory, fileName);
                Assert.That(
                    File.Exists(path),
                    Is.True,
                    $"The diagnostic comparison requires published image {path}."
                );
                encodedImages.Add(fileName, Convert.ToBase64String(File.ReadAllBytes(path)));
            }

            for (int left = 0; left < diagnosticFiles.Length; left++)
            {
                for (int right = left + 1; right < diagnosticFiles.Length; right++)
                {
                    string leftName = diagnosticFiles[left];
                    string rightName = diagnosticFiles[right];
                    Assert.That(
                        encodedImages[leftName],
                        Is.Not.EqualTo(encodedImages[rightName]),
                        $"{leftName} and {rightName} must show different diagnostic states."
                    );
                }
            }
        }

        [Test]
        public void MessageMonitorCaptureSurfacesContainTrafficAndSelectedDetails()
        {
            VisualElement overview = CreateMessageMonitorSurface(showSelectedDetails: false);
            VisualElement selected = CreateMessageMonitorSurface(showSelectedDetails: true);

            Assert.That(
                overview
                    .Query<VisualElement>(className: DxMessagingMessageMonitorWindow.RowClassName)
                    .ToList()
                    .Count,
                Is.EqualTo(3),
                "The overview capture must show one Untargeted, Targeted, and Broadcast message."
            );
            Assert.That(
                overview.Q<ScrollView>(DxMessagingMessageMonitorWindow.ListName),
                Is.Null,
                "The offscreen capture host must replace the nested log ScrollView whose body "
                    + "does not paint without a native docked-window geometry event."
            );
            Label overviewDetails = overview.Q<Label>(
                DxMessagingMessageMonitorWindow.DetailsTypeLabelName
            );
            Assert.That(
                overviewDetails,
                Is.Not.Null,
                "The populated overview must render its newest-message details."
            );
            Assert.That(
                overviewDetails.text,
                Does.Contain("PlayerDamaged"),
                "The overview must render the newest message's real selected-detail pane."
            );
            Label selectedDetails = selected.Q<Label>(
                DxMessagingMessageMonitorWindow.DetailsTypeLabelName
            );
            Assert.That(
                selectedDetails,
                Is.Not.Null,
                "The interaction capture must render the selected message details."
            );
            Assert.That(
                selectedDetails.text,
                Does.Contain("EnemySpawned"),
                "The interaction capture must select a different message row."
            );
            Foldout selectedStack = selected.Q<Foldout>(
                DxMessagingMessageMonitorWindow.DetailsStackFoldoutName
            );
            Assert.That(
                selectedStack,
                Is.Not.Null,
                "The selected message must expose its captured call stack."
            );
            Assert.That(
                selectedStack.value,
                Is.True,
                "The interaction capture must show the expanded stack disclosure."
            );
            Assert.That(
                selected
                    .Q<VisualElement>(DxMessagingMessageMonitorWindow.DetailsPaneName)
                    .Query<ScrollView>()
                    .First(),
                Is.Null,
                "The offscreen capture host must replace the nested details ScrollView so its "
                    + "emission fields and expanded stack paint into the documentation image."
            );
        }

        [Test]
        public void FlowGraphCaptureSurfacesContainSourceDestinationAndRouteDetails()
        {
            FlowGraphSnapshot snapshot = CreateFlowGraphSnapshot();
            VisualElement source = CreateFlowGraphSurface(
                snapshot,
                DocumentationFlowGraphSelection.Message
            );
            VisualElement destination = CreateFlowGraphSurface(
                snapshot,
                DocumentationFlowGraphSelection.Component
            );
            VisualElement route = CreateFlowGraphSurface(
                snapshot,
                DocumentationFlowGraphSelection.Route
            );

            AssertFlowGraphDetailsTitle(source, "PlayerDamaged", "source");
            AssertFlowGraphDetailsTitle(destination, "Player", "destination");
            AssertFlowGraphDetailsTitle(route, "PlayerDamaged -> Player", "route");
            foreach (VisualElement surface in new[] { source, destination, route })
            {
                Foldout evidence = surface.Q<Foldout>(
                    DxMessagingFlowGraphWindow.DetailsEvidenceFoldoutName
                );
                Assert.That(
                    evidence,
                    Is.Not.Null,
                    "Every selected Flow Graph capture must render its evidence disclosure."
                );
                Assert.That(
                    evidence.value,
                    Is.True,
                    "Every selected Flow Graph capture must expand its evidence disclosure."
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
            foreach (EditorSurfaceCaptureResult result in results)
            {
                AssetDatabase.ImportAsset(
                    result.OutputPath,
                    ImportAssetOptions.ForceSynchronousImport
                );
            }

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
                        "Gameplay.HealthComponent",
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
                        "Gameplay.HiddenLifecycleComponent",
                        Dxmsg007FileName,
                        stagingDirectory
                    )
                );
                stagedResults.Add(CaptureIgnoredInspector(component, settings, stagingDirectory));
                stagedResults.Add(
                    CaptureInspectorWarning(
                        component,
                        settings,
                        "DXMSG009",
                        "OnDisable",
                        "Gameplay.ImplicitLifecycleComponent",
                        Dxmsg009FileName,
                        stagingDirectory
                    )
                );
                stagedResults.Add(
                    CaptureInspectorWarning(
                        component,
                        settings,
                        "DXMSG010",
                        "OnDestroy",
                        "Gameplay.TransitiveLifecycleComponent",
                        Dxmsg010FileName,
                        stagingDirectory
                    )
                );
                stagedResults.Add(
                    CaptureFlowGraph(stagingDirectory, DocumentationFlowGraphSelection.None)
                );
                stagedResults.Add(
                    CaptureFlowGraph(stagingDirectory, DocumentationFlowGraphSelection.Component)
                );
                stagedResults.Add(
                    CaptureFlowGraph(stagingDirectory, DocumentationFlowGraphSelection.Message)
                );
                stagedResults.Add(
                    CaptureFlowGraph(stagingDirectory, DocumentationFlowGraphSelection.Route)
                );
                stagedResults.Add(CaptureSubscriptions(component, stagingDirectory));
                stagedResults.Add(
                    CaptureMessageMonitor(stagingDirectory, showSelectedDetails: false)
                );
                stagedResults.Add(
                    CaptureMessageMonitor(stagingDirectory, showSelectedDetails: true)
                );
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
            string fullName,
            string fileName,
            string stagingDirectory
        )
        {
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
                    "Gameplay.OptedOutLifecycleComponent",
                    isFreshThisSession: true
                );
            MessageAwareComponentInspectorViewActions actions = new(
                onOpenScript: _ => { },
                onIgnoreType: _ => { },
                onStopIgnoring: _ => { }
            );
            VisualElement surface = MessageAwareComponentInspectorView.Create(state, actions);
            surface.style.width = 720;
            return Capture(surface, 800, 320, Dxmsg008FileName, stagingDirectory);
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

        private static EditorSurfaceCaptureResult CaptureMessageMonitor(
            string stagingDirectory,
            bool showSelectedDetails
        )
        {
            VisualElement surface = CreateMessageMonitorSurface(showSelectedDetails);
            string fileName = showSelectedDetails
                ? MessageMonitorSelectedFileName
                : MessageMonitorFileName;
            int canvasHeight = showSelectedDetails ? 940 : 820;
            return Capture(surface, 1200, canvasHeight, fileName, stagingDirectory);
        }

        private static VisualElement CreateMessageMonitorSurface(bool showSelectedDetails)
        {
            const string stackTrace =
                "UnityEngine.Debug:ExtractStackTraceNoAlloc (byte*,int,string)\n"
                + "UnityEngine.StackTraceUtility:ExtractStackTrace ()\n"
                + "Gameplay.Combat.DamageSystem:EmitPlayerDamaged () "
                + "(at Assets/Scripts/Combat/DamageSystem.cs:84)\n"
                + "Gameplay.Match.RoundController:ApplyDamage () "
                + "(at Assets/Scripts/Match/RoundController.cs:142)";
            MessageMonitorEntry[] entries =
            {
                new(
                    "PlayerDamaged",
                    "Context: Arena/Player",
                    stackTrace,
                    messageTypeIdentity: "Gameplay.Messages.PlayerDamaged, Gameplay",
                    messageTypeDisplayPath: "Gameplay.Messages.PlayerDamaged",
                    routeKind: "Targeted",
                    traceId: 1042
                ),
                new(
                    "EnemySpawned",
                    "Context: Arena/WaveDirector",
                    "Gameplay.Spawning.WaveDirector:SpawnEnemy () "
                        + "(at Assets/Scripts/Spawning/WaveDirector.cs:117)",
                    messageTypeIdentity: "Gameplay.Messages.EnemySpawned, Gameplay",
                    messageTypeDisplayPath: "Gameplay.Messages.EnemySpawned",
                    routeKind: "Broadcast",
                    traceId: 1041
                ),
                new(
                    "RoundStarted",
                    "Context: none",
                    "Gameplay.Match.RoundController:BeginRound () "
                        + "(at Assets/Scripts/Match/RoundController.cs:62)",
                    messageTypeIdentity: "Gameplay.Messages.RoundStarted, Gameplay",
                    messageTypeDisplayPath: "Gameplay.Messages.RoundStarted",
                    routeKind: "Untargeted",
                    traceId: 1040
                ),
            };
            ComponentMonitorEntry[] components =
            {
                new(
                    "Arena/Player",
                    "PlayerMessagingComponent",
                    activeInHierarchy: true,
                    listenerCount: 3,
                    enabledListenerCount: 3,
                    diagnosticsListenerCount: 3,
                    registrationCount: 4,
                    callCount: 24,
                    localEmissionCount: 6,
                    providerStatusText: "Global bus",
                    warningText: string.Empty
                ),
                new(
                    "UI/HUD",
                    "HudMessagingComponent",
                    activeInHierarchy: true,
                    listenerCount: 2,
                    enabledListenerCount: 2,
                    diagnosticsListenerCount: 2,
                    registrationCount: 3,
                    callCount: 18,
                    localEmissionCount: 0,
                    providerStatusText: "Global bus",
                    warningText: string.Empty
                ),
            };

            VisualElement surface = new();
            surface.style.width = 1120;
            surface.style.height = showSelectedDetails ? 860 : 740;
            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                surface,
                new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 100,
                    entries: entries
                ),
                new MessageMonitorViewState(selectedEntryIndex: showSelectedDetails ? 1 : 0),
                onRefresh: () => { },
                onCopyExport: _ => { },
                componentEntries: components,
                onEnterLiveMode: () => { }
            );

            // The hidden capture host never receives the native docked-window event that makes
            // nested ScrollViews paint their content. Their viewports lay out, but an offscreen
            // panel render produces blank bodies. Keep the shipped rows and detail cards intact,
            // and host them in equivalent clipped containers for this static documentation frame.
            // The Flow Graph only has a top-level ScrollView and does not need this accommodation.
            ReplaceNestedScrollViewForCapture(
                surface.Q<ScrollView>(DxMessagingMessageMonitorWindow.ListName)
            );
            VisualElement detailsPane = surface.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.DetailsPaneName
            );
            ReplaceNestedScrollViewForCapture(detailsPane?.Query<ScrollView>().First());
            if (showSelectedDetails)
            {
                Foldout stack = surface.Q<Foldout>(
                    DxMessagingMessageMonitorWindow.DetailsStackFoldoutName
                );
                if (stack == null)
                {
                    throw new InvalidOperationException(
                        "The selected documentation message must expose its stack trace."
                    );
                }
                stack.value = true;
            }

            // Window roots normally flex to their dock. Pin the requested viewport or the
            // surface grows to the capture canvas instead of retaining documentation dimensions.
            surface.style.flexGrow = 0;
            surface.style.flexShrink = 0;
            return surface;
        }

        private static void ReplaceNestedScrollViewForCapture(ScrollView scrollView)
        {
            if (scrollView?.parent == null)
            {
                throw new InvalidOperationException(
                    "The documentation Monitor must expose each expected nested scroll body."
                );
            }

            VisualElement parent = scrollView.parent;
            int index = parent.IndexOf(scrollView);
            VisualElement clippedContent = new();
            clippedContent.style.flexGrow = 1;
            clippedContent.style.flexShrink = 1;
            clippedContent.style.minHeight = 0;
            clippedContent.style.overflow = Overflow.Hidden;
            while (scrollView.contentContainer.childCount > 0)
            {
                VisualElement child = scrollView.contentContainer[0];
                child.RemoveFromHierarchy();
                clippedContent.Add(child);
            }

            scrollView.RemoveFromHierarchy();
            parent.Insert(index, clippedContent);
        }

        private static EditorSurfaceCaptureResult CaptureFlowGraph(
            string stagingDirectory,
            DocumentationFlowGraphSelection selection
        )
        {
            FlowGraphSnapshot snapshot = CreateFlowGraphSnapshot();
            VisualElement surface = CreateFlowGraphSurface(snapshot, selection);
            string fileName = selection switch
            {
                DocumentationFlowGraphSelection.None => FlowGraphFileName,
                DocumentationFlowGraphSelection.Component => FlowGraphComponentSelectedFileName,
                DocumentationFlowGraphSelection.Message => FlowGraphMessageSelectedFileName,
                DocumentationFlowGraphSelection.Route => FlowGraphRouteSelectedFileName,
                _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, null),
            };
            int surfaceHeight = GetFlowGraphSurfaceHeight(selection);
            return Capture(surface, 1280, surfaceHeight + 80, fileName, stagingDirectory);
        }

        private static VisualElement CreateFlowGraphSurface(
            FlowGraphSnapshot snapshot,
            DocumentationFlowGraphSelection selection
        )
        {
            string selectedItemKey = selection switch
            {
                DocumentationFlowGraphSelection.None => string.Empty,
                DocumentationFlowGraphSelection.Component =>
                    DxMessagingFlowGraphWindow.CreateComponentSelectionKey(
                        snapshot.ComponentNodes[0]
                    ),
                DocumentationFlowGraphSelection.Message =>
                    DxMessagingFlowGraphWindow.CreateMessageSelectionKey(snapshot.MessageNodes[0]),
                DocumentationFlowGraphSelection.Route =>
                    DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(snapshot.Edges[0]),
                _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, null),
            };
            VisualElement surface = new();
            surface.style.width = 1200;
            surface.style.height = GetFlowGraphSurfaceHeight(selection);
            DxMessagingFlowGraphWindow.BuildGraphUi(
                surface,
                snapshot,
                new FlowGraphViewState(selectedItemKey: selectedItemKey),
                onRefresh: () => { },
                onCopyExport: _ => { },
                onSelectionChanged: _ => { }
            );
            if (selection != DocumentationFlowGraphSelection.None)
            {
                Foldout evidence = surface.Q<Foldout>(
                    DxMessagingFlowGraphWindow.DetailsEvidenceFoldoutName
                );
                if (evidence == null)
                {
                    throw new InvalidOperationException(
                        $"The {selection} documentation selection must expose its evidence."
                    );
                }
                evidence.value = true;
            }
            surface.style.flexGrow = 0;
            surface.style.flexShrink = 0;
            return surface;
        }

        private static int GetFlowGraphSurfaceHeight(DocumentationFlowGraphSelection selection)
        {
            return selection switch
            {
                DocumentationFlowGraphSelection.None => 800,
                DocumentationFlowGraphSelection.Message => 1100,
                DocumentationFlowGraphSelection.Component => 1200,
                DocumentationFlowGraphSelection.Route => 1300,
                _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, null),
            };
        }

        private static FlowGraphSnapshot CreateFlowGraphSnapshot()
        {
            const string playerId = "component:player";
            const string hudId = "component:hud";
            FlowGraphComponentNode[] components =
            {
                new(
                    playerId,
                    "Arena/Player",
                    "PlayerMessagingComponent",
                    activeInHierarchy: true,
                    listenerCount: 2,
                    registrationCount: 2,
                    callCount: 18,
                    localMessageCount: 3
                ),
                new(
                    hudId,
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
                    "Gameplay.Messages.PlayerDamaged",
                    2,
                    18,
                    recentGlobalEmissionCount: 6,
                    recentLocalMessageCount: 3,
                    recentTracedDeliveryCount: 6,
                    messageKindName: "TARGETED",
                    recentEmissionSites: new[]
                    {
                        "Gameplay.Combat.DamageSystem.EmitPlayerDamaged (DamageSystem.cs:84)",
                    },
                    recentContexts: new[] { "Arena/Player" },
                    recentContextComponentIds: new Dictionary<string, string>
                    {
                        ["Arena/Player"] = playerId,
                    }
                ),
                new(
                    "Gameplay.Messages.ScoreChanged",
                    2,
                    15,
                    recentGlobalEmissionCount: 5,
                    recentTracedDeliveryCount: 5,
                    messageKindName: "UNTARGETED",
                    recentEmissionSites: new[]
                    {
                        "Gameplay.Scoring.ScoreSystem.EmitScoreChanged (ScoreSystem.cs:51)",
                    }
                ),
            };
            FlowGraphEdge[] edges =
            {
                new(
                    messages[0].MessageTypeName,
                    playerId,
                    "Arena/Player",
                    "Targeted",
                    registrationCount: 1,
                    callCount: 10,
                    recentTracedDeliveryCount: 4,
                    context: "Arena/Player",
                    recentEmissionSites: messages[0].RecentEmissionSites,
                    contextId: 1201
                ),
                new(
                    messages[0].MessageTypeName,
                    hudId,
                    "UI/HUD",
                    "Broadcast",
                    registrationCount: 1,
                    callCount: 8,
                    recentTracedDeliveryCount: 2,
                    recentEmissionSites: messages[0].RecentEmissionSites
                ),
                new(
                    messages[1].MessageTypeName,
                    playerId,
                    "Arena/Player",
                    "Untargeted",
                    registrationCount: 1,
                    callCount: 8,
                    recentTracedDeliveryCount: 3,
                    recentEmissionSites: messages[1].RecentEmissionSites
                ),
                new(
                    messages[1].MessageTypeName,
                    hudId,
                    "UI/HUD",
                    "Untargeted",
                    registrationCount: 1,
                    callCount: 7,
                    recentTracedDeliveryCount: 2,
                    recentEmissionSites: messages[1].RecentEmissionSites
                ),
            };
            FlowGraphTracePath[] tracePaths =
            {
                new(
                    messages[0].MessageTypeName,
                    "Arena/Player",
                    playerId,
                    "Arena/Player",
                    "Targeted",
                    recentTracedDeliveryCount: 4,
                    traceIds: new long[] { 1034, 1037, 1042 },
                    contextId: 1201
                ),
                new(
                    messages[0].MessageTypeName,
                    "Arena/WaveDirector",
                    hudId,
                    "UI/HUD",
                    "Broadcast",
                    recentTracedDeliveryCount: 2,
                    traceIds: new long[] { 1035, 1041 }
                ),
                new(
                    messages[1].MessageTypeName,
                    string.Empty,
                    playerId,
                    "Arena/Player",
                    "Untargeted",
                    recentTracedDeliveryCount: 3,
                    traceIds: new long[] { 1036, 1038, 1040 }
                ),
                new(
                    messages[1].MessageTypeName,
                    string.Empty,
                    hudId,
                    "UI/HUD",
                    "Untargeted",
                    recentTracedDeliveryCount: 2,
                    traceIds: new long[] { 1039, 1040 }
                ),
            };
            return new FlowGraphSnapshot(
                components,
                messages,
                edges,
                tracePaths,
                Array.Empty<string>()
            );
        }

        private static void AssertFlowGraphDetailsTitle(
            VisualElement surface,
            string expectedText,
            string selectionDescription
        )
        {
            Label title = surface.Q<Label>(DxMessagingFlowGraphWindow.DetailsTitleLabelName);
            Assert.That(
                title,
                Is.Not.Null,
                $"The selected {selectionDescription} must render a Flow Graph details pane."
            );
            Assert.That(
                title.text,
                Does.Contain(expectedText),
                $"The {selectionDescription} details title must identify the selected graph item."
            );
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

        private enum DocumentationFlowGraphSelection
        {
            None,
            Component,
            Message,
            Route,
        }
    }

    [AddComponentMenu("")]
    internal sealed class DocumentationMessageAwareComponent : MessageAwareComponent { }
}
#endif
