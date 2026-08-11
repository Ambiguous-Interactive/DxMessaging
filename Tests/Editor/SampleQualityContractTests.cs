#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
#nullable enable annotations
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEditor.SceneManagement;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using Object = UnityEngine.Object;

    [Category("SampleQuality")]
    public sealed class SampleQualityContractTests
    {
        private const string CiImportedSamplesRoot = "Assets/DxmCiSamples";
        private const string CiImportedSamplesCategory = "CiImportedSampleFixture";

        [Test]
        public void RunnableSamplesShipScenesWithResolvableScriptReferences()
        {
            string miniScene = ReadPackageFile("Samples~/Mini Combat/MiniCombat.unity");
            string uiScene = ReadPackageFile(
                "Samples~/UI Buttons + Inspector/UIButtonsInspector.unity"
            );

            AssertSceneReferences(
                miniScene,
                "Samples~/Mini Combat/Boot.cs.meta",
                "Samples~/Mini Combat/Enemy.cs.meta",
                "Samples~/Mini Combat/Player.cs.meta",
                "Samples~/Mini Combat/UIOverlay.cs.meta",
                "Runtime/Unity/MessagingComponent.cs.meta"
            );
            AssertSourceContains(miniScene, "player: {fileID: 1003}", "Mini Combat scene");
            AssertSourceContains(miniScene, "enemy: {fileID: 2002}", "Mini Combat scene");
            AssertSourceContains(miniScene, "uiOverlay: {fileID: 3003}", "Mini Combat scene");

            AssertSceneReferences(
                uiScene,
                "Samples~/UI Buttons + Inspector/MessagingObserver.cs.meta",
                "Samples~/UI Buttons + Inspector/UIButtonEmitter.cs.meta",
                "Runtime/Unity/MessagingComponent.cs.meta"
            );
            AssertSourceContains(uiScene, "showDemoButton: 1", "UI Buttons scene");
            AssertSourceContains(uiScene, "buttonId: Play", "UI Buttons scene");
        }

        [Test]
        [Category(CiImportedSamplesCategory)]
        public void CiImportedRunnableScenesLoadWithoutMissingScripts()
        {
            if (!TryGetImportedSamplesRoot(out string importedSamplesRoot))
            {
                return;
            }

            AssertImportedSceneLoads(importedSamplesRoot, "Mini Combat/MiniCombat.unity");
            AssertImportedSceneLoads(
                importedSamplesRoot,
                "UI Buttons + Inspector/UIButtonsInspector.unity"
            );
        }

        [Test]
        [Category(CiImportedSamplesCategory)]
        public void MiniCombatFallbackDestroysExactlyTheObjectsItCreated()
        {
            if (!TryGetImportedSamplesRoot(out string importedSamplesRoot))
            {
                return;
            }

            Scene originalScene = SceneManager.GetActiveScene();
            Scene isolatedScene = EditorSceneManager.OpenScene(
                $"{importedSamplesRoot}/Mini Combat/MiniCombat.unity",
                OpenSceneMode.Additive
            );
            GameObject? host = null;

            try
            {
                Assert.That(
                    SceneManager.SetActiveScene(isolatedScene),
                    Is.True,
                    "The fallback test must own an isolated active scene."
                );
                Type bootType = FindSceneComponent(isolatedScene, "Boot").GetType();
                host = new GameObject("Mini Combat fallback owner");
                Component boot = host.AddComponent(bootType);
                FieldInfo playerField =
                    bootType.GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Boot must retain its fallback Player.");
                FieldInfo enemyField =
                    bootType.GetField("enemy", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Boot must retain its fallback Enemy.");
                FieldInfo overlayField =
                    bootType.GetField("uiOverlay", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Boot must retain its fallback UI Overlay.");

                if (playerField.GetValue(boot) == null)
                {
                    InvokeRequired(boot, "Awake");
                }

                GameObject[] ownedObjects =
                {
                    ((Component)playerField.GetValue(boot)).gameObject,
                    ((Component)enemyField.GetValue(boot)).gameObject,
                    ((Component)overlayField.GetValue(boot)).gameObject,
                };
                Assert.That(
                    ownedObjects.Select(root => root.name),
                    Is.EquivalentTo(new[] { "Player", "Enemy", "UI Overlay" }),
                    "An unwired Boot must create the three documented fallbacks in its own scene."
                );

                InvokeRequired(boot, "OnDestroy");
                Assert.That(
                    ownedObjects.All(ownedObject => ownedObject == null),
                    Is.True,
                    "Boot.OnDestroy must destroy every fallback it created."
                );
                Assert.That(
                    host,
                    Is.Not.Null,
                    "Fallback cleanup must not destroy the externally owned Boot GameObject."
                );
            }
            finally
            {
                if (host != null)
                {
                    Object.DestroyImmediate(host);
                }

                if (originalScene.IsValid() && originalScene.isLoaded)
                {
                    _ = SceneManager.SetActiveScene(originalScene);
                }

                if (isolatedScene.IsValid() && isolatedScene.isLoaded)
                {
                    _ = EditorSceneManager.CloseScene(isolatedScene, removeScene: true);
                }
            }
        }

        [Test]
        [Category(CiImportedSamplesCategory)]
        public void MiniCombatFallbackPreservesAssignedSceneObjects()
        {
            if (!TryGetImportedSamplesRoot(out string importedSamplesRoot))
            {
                return;
            }

            Scene originalScene = SceneManager.GetActiveScene();
            Scene isolatedScene = EditorSceneManager.OpenScene(
                $"{importedSamplesRoot}/Mini Combat/MiniCombat.unity",
                OpenSceneMode.Additive
            );
            GameObject? host = null;
            GameObject? assignedPlayer = null;
            GameObject? assignedOverlay = null;
            GameObject? createdEnemy = null;

            try
            {
                Assert.That(
                    SceneManager.SetActiveScene(isolatedScene),
                    Is.True,
                    "The mixed-ownership test must own an isolated active scene."
                );
                Type bootType = FindSceneComponent(isolatedScene, "Boot").GetType();
                Type playerType = FindSceneComponent(isolatedScene, "Player").GetType();
                Type overlayType = FindSceneComponent(isolatedScene, "UIOverlay").GetType();
                host = new GameObject("Mini Combat mixed owner");
                assignedPlayer = new GameObject("Scene-owned Player");
                assignedOverlay = new GameObject("Scene-owned UI Overlay");
                Component player = assignedPlayer.AddComponent(playerType);
                Component overlay = assignedOverlay.AddComponent(overlayType);
                Component boot = host.AddComponent(bootType);
                FieldInfo playerField = GetRequiredField(bootType, "player");
                FieldInfo enemyField = GetRequiredField(bootType, "enemy");
                FieldInfo overlayField = GetRequiredField(bootType, "uiOverlay");
                playerField.SetValue(boot, player);
                overlayField.SetValue(boot, overlay);

                InvokeRequired(boot, "Awake");
                createdEnemy = ((Component)enemyField.GetValue(boot)).gameObject;
                Assert.That(
                    playerField.GetValue(boot),
                    Is.SameAs(player),
                    "Boot must retain the assigned Player instead of replacing it."
                );
                Assert.That(
                    overlayField.GetValue(boot),
                    Is.SameAs(overlay),
                    "Boot must retain the assigned UI Overlay instead of replacing it."
                );

                InvokeRequired(boot, "OnDestroy");
                Assert.That(
                    createdEnemy == null,
                    Is.True,
                    "Boot must destroy the missing Enemy fallback that it created."
                );
                Assert.That(
                    assignedPlayer,
                    Is.Not.Null,
                    "Boot must not destroy a scene-owned Player."
                );
                Assert.That(
                    assignedOverlay,
                    Is.Not.Null,
                    "Boot must not destroy a scene-owned UI Overlay."
                );
            }
            finally
            {
                DestroyImmediateIfAlive(host);
                DestroyImmediateIfAlive(assignedPlayer);
                DestroyImmediateIfAlive(assignedOverlay);
                RestoreAndCloseScene(originalScene, isolatedScene);
            }
        }

        [Test]
        [Category(CiImportedSamplesCategory)]
        public void ImportedRunnableScenesExecuteTheirAdvertisedMessageFlows()
        {
            if (!TryGetImportedSamplesRoot(out string importedSamplesRoot))
            {
                return;
            }

            AssertMiniCombatMessageFlow(importedSamplesRoot);
            AssertUiButtonsMessageFlow(importedSamplesRoot);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        [Category(CiImportedSamplesCategory)]
        public void DiagnosticsToolingOwnersRestoreGlobalModeAfterEitherReleaseOrder(
            bool originalMode,
            bool releaseSecondOwnerFirst
        )
        {
            if (!TryGetImportedSamplesRoot(out string importedSamplesRoot))
            {
                return;
            }

            MessageBus messageBus =
                MessageHandler.MessageBus as MessageBus
                ?? throw new AssertionException(
                    "The diagnostics sample requires the default concrete global MessageBus."
                );
            Scene originalScene = SceneManager.GetActiveScene();
            Scene isolatedScene = EditorSceneManager.OpenScene(
                $"{importedSamplesRoot}/Diagnostics Tooling Exerciser/DiagnosticsToolingExerciser.unity",
                OpenSceneMode.Additive
            );
            GameObject? firstHost = null;
            GameObject? secondHost = null;
            Type? exerciserType = null;

            try
            {
                Assert.That(
                    SceneManager.SetActiveScene(isolatedScene),
                    Is.True,
                    "The diagnostics ownership test must own an isolated active scene."
                );
                exerciserType = FindSceneComponent(isolatedScene, "DiagnosticsToolingExerciser")
                    .GetType();
                messageBus.DiagnosticsMode = originalMode;
                firstHost = new GameObject("Diagnostics owner 1");
                secondHost = new GameObject("Diagnostics owner 2");
                Component first = firstHost.AddComponent(exerciserType);
                Component second = secondHost.AddComponent(exerciserType);

                InvokeRequired(first, "ConfigureDiagnostics");
                InvokeRequired(second, "ConfigureDiagnostics");
                Assert.That(
                    messageBus.DiagnosticsMode,
                    Is.True,
                    "Both active tooling owners require global diagnostics."
                );

                Component releasedFirst = releaseSecondOwnerFirst ? second : first;
                Component releasedLast = releaseSecondOwnerFirst ? first : second;
                InvokeRequired(releasedFirst, "ReleaseDiagnosticsLease");
                Assert.That(
                    messageBus.DiagnosticsMode,
                    Is.True,
                    "One remaining tooling owner must keep diagnostics enabled."
                );

                InvokeRequired(releasedLast, "ReleaseDiagnosticsLease");
                Assert.That(
                    messageBus.DiagnosticsMode,
                    Is.EqualTo(originalMode),
                    "The final tooling owner must restore the original global mode."
                );
            }
            finally
            {
                if (firstHost != null && exerciserType != null)
                {
                    InvokeIfPresent(
                        firstHost.GetComponent(exerciserType),
                        "ReleaseDiagnosticsLease"
                    );
                    Object.DestroyImmediate(firstHost);
                }

                if (secondHost != null && exerciserType != null)
                {
                    InvokeIfPresent(
                        secondHost.GetComponent(exerciserType),
                        "ReleaseDiagnosticsLease"
                    );
                    Object.DestroyImmediate(secondHost);
                }

                messageBus.DiagnosticsMode = originalMode;
                if (originalScene.IsValid() && originalScene.isLoaded)
                {
                    _ = SceneManager.SetActiveScene(originalScene);
                }

                if (isolatedScene.IsValid() && isolatedScene.isLoaded)
                {
                    _ = EditorSceneManager.CloseScene(isolatedScene, removeScene: true);
                }
            }
        }

        private static void AssertMiniCombatMessageFlow(string importedSamplesRoot)
        {
            Scene originalScene = SceneManager.GetActiveScene();
            Scene sampleScene = EditorSceneManager.OpenScene(
                $"{importedSamplesRoot}/Mini Combat/MiniCombat.unity",
                OpenSceneMode.Additive
            );
            Component? player = null;
            Component? overlay = null;

            try
            {
                Assert.That(
                    SceneManager.SetActiveScene(sampleScene),
                    Is.True,
                    "The Mini Combat smoke test must activate its imported scene."
                );
                Component boot = FindSceneComponent(sampleScene, "Boot");
                player = FindSceneComponent(sampleScene, "Player");
                overlay = FindSceneComponent(sampleScene, "UIOverlay");

                InvokeRequired(player, "Awake");
                InvokeRequired(overlay, "Awake");
                InvokeRequired(player, "OnEnable");
                InvokeRequired(overlay, "OnEnable");
                FieldInfo hpField = GetRequiredField(player.GetType(), "_hp");
                int initialHp = (int)hpField.GetValue(player);
                InvokeRequired(boot, "Start");

                Assert.That(
                    hpField.GetValue(player),
                    Is.EqualTo(initialHp + 10),
                    "The authored Mini Combat scene must apply its targeted Heal to Player."
                );
                Assert.That(
                    GetRequiredField(overlay.GetType(), "resolutionText").GetValue(overlay),
                    Is.EqualTo("Resolution: 1920 x 1080"),
                    "The authored Mini Combat scene must present the emitted video settings."
                );
                Assert.That(
                    GetRequiredField(overlay.GetType(), "totalDamageObserved").GetValue(overlay),
                    Is.EqualTo(5L),
                    "The authored Mini Combat scene must present the sourced damage event."
                );
            }
            finally
            {
                ReleaseMessageAwareComponent(player);
                ReleaseMessageAwareComponent(overlay);
                RestoreAndCloseScene(originalScene, sampleScene);
            }
        }

        private static void AssertUiButtonsMessageFlow(string importedSamplesRoot)
        {
            Scene originalScene = SceneManager.GetActiveScene();
            Scene sampleScene = EditorSceneManager.OpenScene(
                $"{importedSamplesRoot}/UI Buttons + Inspector/UIButtonsInspector.unity",
                OpenSceneMode.Additive
            );
            Component? observer = null;

            try
            {
                Assert.That(
                    SceneManager.SetActiveScene(sampleScene),
                    Is.True,
                    "The UI Buttons smoke test must activate its imported scene."
                );
                observer = FindSceneComponent(sampleScene, "MessagingObserver");
                Type observerType = observer.GetType();
                Component emitter = FindSceneComponent(sampleScene, "UIButtonEmitter");
                GetRequiredField(observerType, "logTypedClicks").SetValue(observer, false);

                InvokeRequired(observer, "Awake");
                InvokeRequired(observer, "OnEnable");
                InvokePublic(emitter, "Click");

                Assert.That(
                    GetRequiredField(observerType, "typedClickCount").GetValue(observer),
                    Is.EqualTo(1),
                    "The built-in UI button must deliver one typed ButtonClicked event."
                );
                Assert.That(
                    GetRequiredField(observerType, "acceptAllCount").GetValue(observer),
                    Is.EqualTo(2),
                    "The observer must see both the broadcast and targeted demo routes."
                );
                Assert.That(
                    GetRequiredField(observerType, "lastButtonId").GetValue(observer),
                    Is.EqualTo("Play"),
                    "The authored button ID must reach the typed observer."
                );
                Assert.That(
                    GetRequiredField(observerType, "lastButtonSource").GetValue(observer),
                    Is.EqualTo((InstanceId)emitter.gameObject),
                    "The clicked GameObject must remain the ButtonClicked broadcast source."
                );
            }
            finally
            {
                ReleaseMessageAwareComponent(observer);
                RestoreAndCloseScene(originalScene, sampleScene);
            }
        }

        [Test]
        public void MiniCombatFallbackTracksAndReleasesOnlyCreatedObjects()
        {
            string boot = ReadPackageFile("Samples~/Mini Combat/Boot.cs");

            AssertSourceContains(boot, "out createdPlayer", "Mini Combat Boot");
            AssertSourceContains(boot, "out createdEnemy", "Mini Combat Boot");
            AssertSourceContains(boot, "out createdUiOverlay", "Mini Combat Boot");
            AssertSourceContains(boot, "private void OnDestroy()", "Mini Combat Boot");
            AssertSourceContains(boot, "DestroyOwned(createdPlayer);", "Mini Combat Boot");
            AssertSourceContains(boot, "DestroyOwned(createdEnemy);", "Mini Combat Boot");
            AssertSourceContains(boot, "DestroyOwned(createdUiOverlay);", "Mini Combat Boot");
            AssertSourceOmits(boot, "FindObjectOfType", "Mini Combat Boot");
            AssertSourceOmits(boot, "FindObjectsByType", "Mini Combat Boot");
        }

        [Test]
        public void DiagnosticsSamplesRestoreGlobalStateAndUiNeedsNoManualHookup()
        {
            string observer = ReadPackageFile(
                "Samples~/UI Buttons + Inspector/MessagingObserver.cs"
            );
            string emitter = ReadPackageFile("Samples~/UI Buttons + Inspector/UIButtonEmitter.cs");
            string tooling = ReadPackageFile(
                "Samples~/Diagnostics Tooling Exerciser/DiagnosticsToolingExerciser.cs"
            );
            string toolingGuide = ReadPackageFile(
                "Samples~/Diagnostics Tooling Exerciser/Editor/DiagnosticsToolingGuideWindow.cs"
            );

            AssertSourceContains(observer, "Token.DiagnosticMode = true;", "UI observer");
            AssertSourceOmits(observer, "MessageHandler.MessageBus", "UI observer");
            AssertSourceContains(emitter, "private void OnGUI()", "UI emitter");
            AssertSourceContains(emitter, "GUI.Button", "UI emitter");
            AssertSourceOmits(emitter, "Hook this to a Unity UI Button", "UI emitter");

            AssertSourceContains(tooling, "originalDiagnosticsMode", "Diagnostics tooling");
            AssertSourceContains(tooling, "ReleaseDiagnosticsLease();", "Diagnostics tooling");
            AssertSourceContains(tooling, "RestoreDiagnosticsMode();", "Diagnostics tooling");
            AssertSourceOmits(tooling, "new[] { gameObject }", "Diagnostics tooling");
            AssertSourceContains(toolingGuide, "private void OnDisable()", "Tooling guide");
            AssertSourceContains(toolingGuide, "_statusRefresh?.Pause();", "Tooling guide");
            AssertSourceContains(toolingGuide, "_statusRefresh = null;", "Tooling guide");
            AssertSourceContains(toolingGuide, "if (Application.isBatchMode)", "Tooling guide");
            AssertSourceContains(
                toolingGuide,
                "AssemblyReloadEvents.beforeAssemblyReload += Shutdown;",
                "Tooling guide"
            );
            AssertSourceContains(
                toolingGuide,
                "EditorApplication.quitting += Shutdown;",
                "Tooling guide"
            );
            AssertSourceContains(toolingGuide, "private static void Shutdown()", "Tooling guide");
            AssertSourceContains(
                toolingGuide,
                "EditorSceneManager.sceneOpened -= HandleSceneOpened;",
                "Tooling guide"
            );
            AssertSourceContains(
                toolingGuide,
                "EditorApplication.delayCall -= OpenForActiveSampleSceneOnce;",
                "Tooling guide"
            );
        }

        [Test]
        public void DependencyInjectionSamplesBoundWorkAndDisposeRegistrationLeases()
        {
            string vContainer = ReadPackageFile("Samples~/DI/VContainer/SampleLifetimeScope.cs");
            string zenject = ReadPackageFile("Samples~/DI/Zenject/SampleInstaller.cs");
            string reflex = ReadPackageFile("Samples~/DI/Reflex/SampleInstaller.cs");

            AssertSourceContains(vContainer, "EmitIntervalSeconds", "VContainer sample");
            AssertSourceContains(
                vContainer,
                "Time.unscaledTime < nextEmitTime",
                "VContainer sample"
            );
            AssertSourceContains(
                vContainer,
                "private readonly IMessageBus messageBus;",
                "VContainer sample"
            );
            AssertSourceContains(vContainer, "IMessageBus messageBus,", "VContainer sample");
            AssertSourceContains(vContainer, "message.Emit(messageBus);", "VContainer sample");
            AssertSourceOmits(vContainer, "message.Emit();", "VContainer sample");
            AssertSourceContains(vContainer, "lease.Dispose();", "VContainer sample");
            AssertSourceContains(zenject, "lease.Dispose();", "Zenject sample");
            AssertSourceContains(reflex, "_lease.Dispose();", "Reflex sample");
            AssertSourceContains(
                reflex,
                "builder.OnContainerBuilt += OnContainerBuilt;",
                "Reflex sample"
            );
            AssertSourceContains(
                reflex,
                "_builder.OnContainerBuilt -= OnContainerBuilt;",
                "Reflex sample"
            );
            AssertSourceContains(reflex, "UnsubscribeFromContainerBuilt();", "Reflex sample");
        }

        [Test]
        public void RunnableSampleReadmesPromiseOnlyZeroTouchImportedFlows()
        {
            string miniReadme = ReadPackageFile("Samples~/Mini Combat/README.md");
            string uiReadme = ReadPackageFile("Samples~/UI Buttons + Inspector/README.md");
            string diReadme = ReadPackageFile("Samples~/DI/README.md");
            string miniOverlay = ReadPackageFile("Samples~/Mini Combat/UIOverlay.cs");
            string packageJson = ReadPackageFile("package.json");
            string gettingStarted = ReadPackageFile("docs/getting-started/index.md");

            AssertSourceContains(miniReadme, "Open `MiniCombat.unity`", "Mini Combat README");
            AssertSourceContains(miniReadme, "The scene is already wired.", "Mini Combat README");
            AssertSourceContains(uiReadme, "Open `UIButtonsInspector.unity`", "UI Buttons README");
            AssertSourceContains(uiReadme, "needs no", "UI Buttons README");
            AssertSourceOmits(uiReadme, "Button click does nothing", "UI Buttons README");
            AssertSourceOmits(uiReadme, "Confirm the Button's `On Click ()`", "UI Buttons README");
            AssertSourceContains(diReadme, "prefab does not register `IMessageBus`", "DI README");
            AssertSourceContains(diReadme, "resolve the same `IMessageBus`", "DI README");
            AssertSourceOmits(diReadme, "Because the prefab already configured", "DI README");
            AssertSourceContains(
                miniOverlay,
                "GUI.Box(overlayRect, \"Combat Feed\")",
                "Mini Combat UI Overlay"
            );
            AssertSourceContains(
                miniOverlay,
                "totalDamageObserved += damage;",
                "Mini Combat UI Overlay"
            );
            AssertSourceOmits(miniOverlay, "Debug.Log", "Mini Combat UI Overlay");
            AssertSourceContains(
                packageJson,
                "token-local diagnostics disposed with the observer",
                "package sample description"
            );
            AssertSourceContains(
                gettingStarted,
                "token-local diagnostics that are disposed with the observer",
                "getting-started sample description"
            );
            AssertSourceOmits(packageJson, "restore global state", "package sample description");
            AssertSourceOmits(
                gettingStarted,
                "restore the previous global setting",
                "getting-started sample description"
            );
        }

        private static void AssertSceneReferences(string scene, params string[] metaPaths)
        {
            foreach (string metaPath in metaPaths)
            {
                string meta = ReadPackageFile(metaPath);
                Match match = Regex.Match(meta, "^guid: ([0-9a-f]{32})$", RegexOptions.Multiline);
                Assert.That(match.Success, Is.True, $"Missing Unity GUID in {metaPath}.");
                Assert.That(
                    scene,
                    Does.Contain($"guid: {match.Groups[1].Value}"),
                    $"The scene must reference the script GUID declared by {metaPath}."
                );
            }
        }

        private static void AssertImportedSceneLoads(
            string importedSamplesRoot,
            string relativePath
        )
        {
            string scenePath = $"{importedSamplesRoot}/{relativePath}";
            string absoluteScenePath = Path.Combine(
                Application.dataPath,
                importedSamplesRoot
                    .Substring("Assets/".Length)
                    .Replace('/', Path.DirectorySeparatorChar),
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            );
            Assert.That(
                File.Exists(absoluteScenePath),
                Is.True,
                $"The CI sample fixture must copy {scenePath}."
            );

            Scene scene = default;
            try
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                Assert.That(scene.IsValid(), Is.True, $"Unity must open {scenePath}.");
                Assert.That(scene.isLoaded, Is.True, $"Unity must load {scenePath}.");

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    {
                        Assert.That(
                            GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(
                                child.gameObject
                            ),
                            Is.Zero,
                            $"{scenePath} contains a missing script on '{child.name}'."
                        );
                    }
                }
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    _ = EditorSceneManager.CloseScene(scene, removeScene: true);
                }
            }
        }

        private static bool TryGetImportedSamplesRoot(out string importedSamplesRoot)
        {
            importedSamplesRoot = CiImportedSamplesRoot;
            bool fixtureExists = AssetDatabase.IsValidFolder(importedSamplesRoot);
            Assert.That(
                fixtureExists || !IsContinuousIntegration(),
                Is.True,
                "CI must import Assets/DxmCiSamples before running fixture-gated sample contracts."
            );
            if (!fixtureExists)
            {
                Assert.Inconclusive(
                    "This fixture-gated contract runs when the Unity matrix imports Assets/DxmCiSamples."
                );
            }
            return fixtureExists;
        }

        private static bool IsContinuousIntegration()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("CI"),
                "true",
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static Component FindSceneComponent(Scene scene, string componentTypeName)
        {
            Component? component = scene
                .GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Component>(includeInactive: true))
                .FirstOrDefault(candidate =>
                    candidate != null && candidate.GetType().Name == componentTypeName
                );
            return component
                ?? throw new AssertionException(
                    $"The imported scene {scene.path} must contain {componentTypeName}."
                );
        }

        private static FieldInfo GetRequiredField(Type type, string fieldName)
        {
            return type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException($"{type.Name} must retain its {fieldName} field.");
        }

        private static void InvokePublic(Component component, string methodName)
        {
            MethodInfo method =
                component
                    .GetType()
                    .GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new AssertionException(
                    $"{component.GetType().Name} must expose {methodName}()."
                );
            _ = method.Invoke(component, null);
        }

        private static void ReleaseMessageAwareComponent(Component? component)
        {
            if (component == null)
            {
                return;
            }

            InvokeIfPresent(component, "OnDisable");
            InvokeIfPresent(component, "OnDestroy");
        }

        private static void DestroyImmediateIfAlive(GameObject? gameObject)
        {
            if (gameObject != null)
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        private static void RestoreAndCloseScene(Scene originalScene, Scene ownedScene)
        {
            if (originalScene.IsValid() && originalScene.isLoaded)
            {
                _ = SceneManager.SetActiveScene(originalScene);
            }

            if (ownedScene.IsValid() && ownedScene.isLoaded)
            {
                _ = EditorSceneManager.CloseScene(ownedScene, removeScene: true);
            }
        }

        private static void AssertSourceContains(string source, string expected, string owner)
        {
            Assert.That(
                source,
                Does.Contain(expected),
                $"{owner} must contain the contract fragment: {expected}"
            );
        }

        private static void AssertSourceOmits(string source, string forbidden, string owner)
        {
            Assert.That(
                source,
                Does.Not.Contain(forbidden),
                $"{owner} must omit the unsafe or stale fragment: {forbidden}"
            );
        }

        private static void InvokeRequired(Component component, string methodName)
        {
            MethodInfo method =
                component
                    .GetType()
                    .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException(
                    $"{component.GetType().Name} must declare {methodName}()."
                );
            _ = method.Invoke(component, null);
        }

        private static void InvokeIfPresent(Component component, string methodName)
        {
            MethodInfo? method = component
                .GetType()
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            _ = method?.Invoke(component, null);
        }

        private static string ReadPackageFile(string relativePath)
        {
            string packageRoot = Path.GetFullPath(
                Path.Combine(
                    Application.dataPath,
                    "..",
                    "Packages",
                    "com.wallstop-studios.dxmessaging"
                )
            );
            return File.ReadAllText(Path.Combine(packageRoot, relativePath));
        }
    }
}
#endif
