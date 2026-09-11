#if UNITY_EDITOR
namespace WallstopStudios.DxMessagingSamples.DiagnosticsToolingExerciser.Editor
{
    using System;
    using System.Linq;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Windows;
    using UnityEditor;
    using UnityEditor.SceneManagement;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    internal sealed class DiagnosticsToolingGuideWindow : EditorWindow
    {
        private const string MenuPath =
            "Tools/Wallstop Studios/DxMessaging/Diagnostics Tooling Guided Tour";
        private const string SettingsPath = "Project/Wallstop Studios/DxMessaging";

        private Label _liveStatus;
        private Button _playButton;
        private Button _emitButton;
        private Button _resetAndEmitButton;
        private Button _disableReceiverButton;
        private Button _releaseTokenButton;
        private Button _destroyReceiverButton;
        private Button _separateBusButton;
        private IVisualElementScheduledItem _statusRefresh;

        [MenuItem(MenuPath)]
        internal static void Open()
        {
            DiagnosticsToolingGuideWindow window = GetWindow<DiagnosticsToolingGuideWindow>();
            window.titleContent = new GUIContent("DxMessaging Guided Tour");
            window.minSize = new Vector2(420f, 540f);
            window.Show();
        }

        private void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();
            DxMessagingEditorTheme.ApplyWindow(root);
            root.AddToClassList("dx-tooling-guide");

            Label title = new("Diagnostics Tooling Guided Tour");
            title.AddToClassList(DxMessagingEditorTheme.DetailTitleClassName);
            root.Add(title);
            root.Add(
                new Label(
                    "Follow these steps in order. The status panel updates while the sample runs."
                )
            );

            _liveStatus = new Label { name = "dx-tooling-guide-status" };
            _liveStatus.AddToClassList(DxMessagingEditorTheme.CardClassName);
            root.Add(_liveStatus);

            ScrollView steps = new();
            steps.style.flexGrow = 1f;
            root.Add(steps);

            _playButton = AddStep(
                steps,
                "1. Start the deterministic scene",
                "Enter Play Mode. The default burst creates three sequences across every route kind.",
                "Enter Play Mode",
                TogglePlayMode
            );
            _playButton.name = "dx-tooling-guide-play";
            _emitButton = AddStep(
                steps,
                "2. Add one trace sequence",
                "Emit one untargeted pulse, targeted commands, and sourced broadcasts.",
                "Emit One Of Each",
                EmitOneOfEach
            );
            _emitButton.name = "dx-tooling-guide-emit";
            _resetAndEmitButton = AddStep(
                steps,
                "3. Reset receiver counters",
                "Clear receiver counters and emit the default burst. Message history stays visible and trace IDs continue forward.",
                "Reset Counters And Emit Burst",
                ResetCountersAndEmitBurst
            );
            _resetAndEmitButton.name = "dx-tooling-guide-reset";
            AddStep(
                steps,
                "4. Inspect message history",
                "Open Message Monitor and filter by ToolingPulse, ToolingCommand, ToolingSignal, or a sample trace ID.",
                "Open Message Monitor",
                DxMessagingMessageMonitorWindow.Open
            );
            AddStep(
                steps,
                "5. Explore live routing",
                "Open Flow Graph, then select a route to inspect evidence, activity, targets, and source links.",
                "Open Flow Graph",
                DxMessagingFlowGraphWindow.Open
            );
            AddStep(
                steps,
                "6. Inspect component diagnostics",
                "Select all receivers to compare enabled tokens, route registrations, and live counters in the Inspector.",
                "Select All Receivers",
                SelectAllReceivers
            );
            _disableReceiverButton = AddStep(
                steps,
                "7. Diagnose a disabled receiver",
                "Disable Player Ship, then refresh Flow Graph and inspect it. Its token stays visible but reports Disabled; new messages do not reach it.",
                "Disable Player Ship",
                DisablePlayerShip
            );
            _disableReceiverButton.name = "dx-tooling-guide-disable-receiver";
            _releaseTokenButton = AddStep(
                steps,
                "8. Diagnose a missing token",
                "Release Enemy Drone's token, then refresh Flow Graph and inspect it. The Inspector reports No token and its routes disappear.",
                "Release Enemy Drone Token",
                ReleaseEnemyDroneToken
            );
            _releaseTokenButton.name = "dx-tooling-guide-release-token";
            _destroyReceiverButton = AddStep(
                steps,
                "9. Diagnose destroyed and stale evidence",
                "Select a HUD Console route in Flow Graph first, then destroy the receiver. Refresh removes the stale selection and live routes; captured Message Monitor context stays readable without a dead action.",
                "Destroy HUD Console",
                DestroyHudConsole
            );
            _destroyReceiverButton.name = "dx-tooling-guide-destroy-receiver";
            _separateBusButton = AddStep(
                steps,
                "10. Prove separate-bus visibility boundaries",
                "Emit through a separate MessageBus and standalone token. The status panel shows its call count and retained registration evidence, but Message Monitor and Flow Graph must not gain a route or emission.",
                "Emit On Separate Bus",
                EmitOnSeparateBus
            );
            _separateBusButton.name = "dx-tooling-guide-separate-bus";
            AddStep(
                steps,
                "11. Change capture policy and reset",
                "Open Project Settings to adjust diagnostics targets and buffer size. Exit and re-enter Play Mode to restore all three receivers.",
                "Open DxMessaging Settings",
                () => SettingsService.OpenProjectSettings(SettingsPath)
            );

            _statusRefresh?.Pause();
            _statusRefresh = root.schedule.Execute(RefreshStatus).Every(250L);
            RefreshStatus();
        }

        private void OnDisable()
        {
            _statusRefresh?.Pause();
            _statusRefresh = null;
        }

        private static Button AddStep(
            VisualElement parent,
            string heading,
            string body,
            string buttonText,
            Action action
        )
        {
            VisualElement card = new();
            card.AddToClassList(DxMessagingEditorTheme.CardClassName);
            Label headingLabel = new(heading);
            headingLabel.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            card.Add(headingLabel);
            card.Add(new Label(body));
            Button button = new(action) { text = buttonText };
            button.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            card.Add(button);
            parent.Add(card);
            return button;
        }

        private void RefreshStatus()
        {
            DiagnosticsToolingExerciser runner = FindRunner();
            DiagnosticsToolingReceiver[] receivers = FindReceivers();
            DiagnosticsToolingReceiver playerShip = FindReceiver(receivers, "Player Ship");
            DiagnosticsToolingReceiver enemyDrone = FindReceiver(receivers, "Enemy Drone");
            DiagnosticsToolingReceiver hudConsole = FindReceiver(receivers, "HUD Console");
            bool canEmit = EditorApplication.isPlaying && runner != null;
            _playButton.text = EditorApplication.isPlaying ? "Exit Play Mode" : "Enter Play Mode";
            _emitButton.SetEnabled(canEmit);
            _resetAndEmitButton.SetEnabled(canEmit);
            _disableReceiverButton.SetEnabled(canEmit && playerShip != null && playerShip.enabled);
            _releaseTokenButton.SetEnabled(
                canEmit && enemyDrone != null && enemyDrone.Token != null
            );
            _destroyReceiverButton.SetEnabled(canEmit && hudConsole != null);
            _separateBusButton.SetEnabled(canEmit);

            string receiverSummary =
                receivers.Length == 0
                    ? "No active receivers found."
                    : string.Join(
                        "\n",
                        receivers.Select(receiver =>
                            $"{receiver.ListenerLabel}: {GetTokenStatus(receiver)}, U {receiver.UntargetedCount}, T {receiver.TargetedCount}, B {receiver.BroadcastCount}, Any {receiver.GlobalAcceptAllCount}"
                        )
                    );
            string runnerSummary =
                runner == null
                    ? "Runner: not active"
                    : $"Runner: sequence {runner.Sequence} - {runner.LastRunSummary}\nSeparate bus: token {(runner.StandaloneTokenEnabled ? "enabled" : "missing")}, registrations {runner.SeparateBusRegistrationCount}, log entries {runner.SeparateBusLogCount}, calls {runner.SeparateBusCallCount}, last trace {runner.LastSeparateBusTraceId}";
            _liveStatus.text =
                $"STATUS\nPlay Mode: {(EditorApplication.isPlaying ? "running" : "stopped")}\n{runnerSummary}\n{receiverSummary}";
        }

        private static void TogglePlayMode()
        {
            EditorApplication.isPlaying = !EditorApplication.isPlaying;
        }

        private static void EmitOneOfEach()
        {
            DiagnosticsToolingExerciser runner = FindRunner();
            if (runner != null)
            {
                runner.EmitOneOfEach();
            }
        }

        private static void ResetCountersAndEmitBurst()
        {
            DiagnosticsToolingExerciser runner = FindRunner();
            if (runner != null)
            {
                runner.ResetCountersAndEmitBurst();
            }
        }

        private static void EmitOnSeparateBus()
        {
            DiagnosticsToolingExerciser runner = FindRunner();
            if (runner != null)
            {
                runner.EmitOnSeparateBus();
            }
        }

        private static void SelectAllReceivers()
        {
            Selection.objects = FindReceivers().Cast<Object>().ToArray();
        }

        private static void DisablePlayerShip()
        {
            DiagnosticsToolingReceiver receiver = FindReceiver("Player Ship");
            if (receiver == null)
            {
                return;
            }

            receiver.enabled = false;
            Selection.activeObject = receiver;
        }

        private static void ReleaseEnemyDroneToken()
        {
            DiagnosticsToolingReceiver receiver = FindReceiver("Enemy Drone");
            if (receiver == null)
            {
                return;
            }

            receiver.ReleaseTokenForWalkthrough();
            Selection.activeObject = receiver;
        }

        private static void DestroyHudConsole()
        {
            DiagnosticsToolingReceiver receiver = FindReceiver("HUD Console");
            if (receiver != null)
            {
                Object.Destroy(receiver.gameObject);
            }
        }

        private static string GetTokenStatus(DiagnosticsToolingReceiver receiver)
        {
            if (receiver.Token == null)
            {
                return "token missing";
            }

            return receiver.Token.Enabled ? "token enabled" : "token disabled";
        }

        private static DiagnosticsToolingReceiver FindReceiver(string listenerLabel)
        {
            return FindReceiver(FindReceivers(), listenerLabel);
        }

        private static DiagnosticsToolingReceiver FindReceiver(
            DiagnosticsToolingReceiver[] receivers,
            string listenerLabel
        )
        {
            return receivers.FirstOrDefault(receiver =>
                string.Equals(receiver.ListenerLabel, listenerLabel, StringComparison.Ordinal)
            );
        }

        private static DiagnosticsToolingExerciser FindRunner()
        {
#if UNITY_6000_5_OR_NEWER
            return Object.FindAnyObjectByType<DiagnosticsToolingExerciser>(
                FindObjectsInactive.Exclude
            );
#elif UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<DiagnosticsToolingExerciser>(
                FindObjectsInactive.Exclude
            );
#else
            return Object.FindObjectOfType<DiagnosticsToolingExerciser>();
#endif
        }

        private static DiagnosticsToolingReceiver[] FindReceivers()
        {
#if UNITY_6000_5_OR_NEWER
            return Object.FindObjectsByType<DiagnosticsToolingReceiver>(
                FindObjectsInactive.Exclude
            );
#elif UNITY_2023_1_OR_NEWER
            return Object.FindObjectsByType<DiagnosticsToolingReceiver>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );
#else
            return Object.FindObjectsOfType<DiagnosticsToolingReceiver>();
#endif
        }
    }

    [InitializeOnLoad]
    internal static class DiagnosticsToolingGuideBootstrap
    {
        private const string ScenePathSuffix =
            "/Diagnostics Tooling Exerciser/DiagnosticsToolingExerciser.unity";
        private const string SessionKey = "DxMessaging.DiagnosticsToolingGuide.Opened";

        static DiagnosticsToolingGuideBootstrap()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            Shutdown();
            EditorSceneManager.sceneOpened += HandleSceneOpened;
            EditorApplication.delayCall += OpenForActiveSampleSceneOnce;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        private static void Shutdown()
        {
            EditorSceneManager.sceneOpened -= HandleSceneOpened;
            EditorApplication.delayCall -= OpenForActiveSampleSceneOnce;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
        }

        private static void HandleSceneOpened(Scene scene, OpenSceneMode mode)
        {
            OpenForSceneOnce(scene);
        }

        private static void OpenForActiveSampleSceneOnce()
        {
            OpenForSceneOnce(SceneManager.GetActiveScene());
        }

        private static void OpenForSceneOnce(Scene scene)
        {
            if (!TryClaimSessionForScene(scene.path))
            {
                return;
            }

            DiagnosticsToolingGuideWindow.Open();
        }

        internal static bool TryClaimSessionForScene(string scenePath)
        {
            string normalizedPath = (scenePath ?? string.Empty).Replace('\\', '/');
            if (
                SessionState.GetBool(SessionKey, false)
                || !normalizedPath.EndsWith(ScenePathSuffix, StringComparison.Ordinal)
            )
            {
                return false;
            }

            SessionState.SetBool(SessionKey, true);
            return true;
        }
    }
}
#endif
