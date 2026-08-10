#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
#nullable enable
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.UIElements;

    public sealed class DiagnosticsToolingSampleContractTests
    {
        private const string SampleRelativePath = "Samples~/Diagnostics Tooling Exerciser";
        private const string SceneFileName = "DiagnosticsToolingExerciser.unity";
        private const string RunnerScriptFileName = "DiagnosticsToolingExerciser.cs";
        private const string ReceiverScriptFileName = "DiagnosticsToolingReceiver.cs";
        private const string MessagesScriptFileName = "Messages.cs";
        private const string GuideScriptFileName = "Editor/DiagnosticsToolingGuideWindow.cs";
        private const string RuntimeMessagingComponentGuid = "98ea04ea326660845ba49942dacbf907";

        [Test]
        public void DiagnosticsToolingSampleIsRegisteredInPackageManifest()
        {
            string packageJson = ReadPackageFile("package.json");

            Assert.That(
                packageJson,
                Does.Contain("\"displayName\": \"Diagnostics Tooling Exerciser\"")
            );
            Assert.That(
                packageJson,
                Does.Contain("\"path\": \"Samples~/Diagnostics Tooling Exerciser\"")
            );
        }

        [Test]
        public void SceneReferencesRunnerReceiverAndMessagingComponentScriptsByGuid()
        {
            string scene = ReadSampleFile(SceneFileName);
            string runnerGuid = ReadGuid(RunnerScriptFileName + ".meta");
            string receiverGuid = ReadGuid(ReceiverScriptFileName + ".meta");

            Assert.That(scene, Does.Contain($"guid: {runnerGuid}"));
            Assert.That(scene, Does.Contain($"guid: {receiverGuid}"));
            Assert.That(scene, Does.Contain($"guid: {RuntimeMessagingComponentGuid}"));
            Assert.That(CountOccurrences(scene, $"guid: {receiverGuid}"), Is.EqualTo(3));
            Assert.That(
                CountOccurrences(scene, $"guid: {RuntimeMessagingComponentGuid}"),
                Is.EqualTo(3)
            );
        }

        [Test]
        public void ScenePinsDeterministicToolingTopology()
        {
            string scene = ReadSampleFile(SceneFileName);

            Assert.That(scene, Does.Contain("m_Name: DxMessaging Tooling Exerciser"));
            Assert.That(scene, Does.Contain("m_Name: Player Ship"));
            Assert.That(scene, Does.Contain("m_Name: Enemy Drone"));
            Assert.That(scene, Does.Contain("m_Name: HUD Console"));
            Assert.That(scene, Does.Contain("enableGlobalDiagnostics: 1"));
            Assert.That(scene, Does.Contain("emitOnStart: 1"));
            Assert.That(scene, Does.Contain("burstCount: 3"));
            Assert.That(scene, Does.Contain("registerGlobalAcceptAll: 1"));
            Assert.That(scene, Does.Contain("enableLocalDiagnostics: 1"));
            Assert.That(scene, Does.Contain("broadcastSourceFilter: {fileID: 120000}"));
            Assert.That(scene, Does.Contain("broadcastSourceFilter: {fileID: 130000}"));
        }

        [Test]
        public void SampleScriptsCoverEveryDiagnosticsRouteKind()
        {
            string runner = ReadSampleFile(RunnerScriptFileName);
            string receiver = ReadSampleFile(ReceiverScriptFileName);
            string messages = ReadSampleFile(MessagesScriptFileName);

            Assert.That(messages, Does.Contain("IUntargetedMessage<ToolingPulse>"));
            Assert.That(messages, Does.Contain("ITargetedMessage<ToolingCommand>"));
            Assert.That(messages, Does.Contain("IBroadcastMessage<ToolingSignal>"));
            Assert.That(runner, Does.Contain("EmitUntargeted"));
            Assert.That(runner, Does.Contain("EmitGameObjectTargeted"));
            Assert.That(runner, Does.Contain("SourcedBroadcast"));
            Assert.That(receiver, Does.Contain("RegisterUntargeted<ToolingPulse>"));
            Assert.That(receiver, Does.Contain("RegisterGameObjectTargeted<ToolingCommand>"));
            Assert.That(receiver, Does.Contain("RegisterBroadcastWithoutSource<ToolingSignal>"));
            Assert.That(receiver, Does.Contain("RegisterBroadcast<ToolingSignal>"));
            Assert.That(receiver, Does.Contain("RegisterGlobalAcceptAll"));
            Assert.That(receiver, Does.Contain("Token.DiagnosticMode = true"));
        }

        [Test]
        public void SampleLifecycleRestoresRegistrationsAcrossPlayStartupAndActivation()
        {
            string runner = ReadSampleFile(RunnerScriptFileName);
            string receiver = ReadSampleFile(ReceiverScriptFileName);

            Assert.That(receiver, Does.Contain("protected override void OnEnable()"));
            Assert.That(receiver, Does.Contain("EnsureToolingRegistrations();"));
            Assert.That(receiver, Does.Contain("EnsureToolingRegistrations(force: true);"));
            Assert.That(receiver, Does.Contain("PreparedReceiverIds.Add(((InstanceId)this).Id)"));
            Assert.That(receiver, Does.Contain("messagingComponent.Release(this);"));
            Assert.That(receiver, Does.Contain("messagingComponent.Create(this)"));
            Assert.That(
                receiver.IndexOf(
                    "messagingComponent.Release(this);",
                    System.StringComparison.Ordinal
                ),
                Is.LessThan(
                    receiver.IndexOf(
                        "messagingComponent.Create(this)",
                        System.StringComparison.Ordinal
                    )
                ),
                "The sample must discard a possibly stale enabled token before creating its live token."
            );
            Assert.That(receiver, Does.Not.Contain("_messageRegistrationToken.Enabled"));
            Assert.That(receiver, Does.Contain("RestoreRegistrationsAfterSceneLoad"));
            Assert.That(runner, Does.Contain("private void OnEnable()"));
            Assert.That(runner, Does.Contain("InitializeAfterSceneLoad"));
            Assert.That(runner, Does.Contain("InitializedRunnerIds.Add(((InstanceId)this).Id)"));
            Assert.That(runner, Does.Contain("InitializedRunnerIds.Remove(((InstanceId)this).Id)"));
            Assert.That(runner, Does.Contain("BeginPlaySession"));
            Assert.That(runner, Does.Not.Contain("private void Start()"));
            Assert.That(runner, Does.Not.Contain("EnsureReceiversReady"));
            Assert.That(receiver, Does.Contain("base.Awake();"));
            Assert.That(receiver, Does.Contain("base.OnEnable();"));
            Assert.That(receiver, Does.Contain("base.OnDisable();"));
            Assert.That(
                receiver,
                Does.Contain("PreparedReceiverIds.Remove(((InstanceId)this).Id)")
            );
            Assert.That(receiver, Does.Contain("base.OnDestroy();"));
        }

        [Test]
        public void SampleReadmeDocumentsToolVerificationWorkflow()
        {
            string readme = ReadSampleFile("README.md");

            Assert.That(readme, Does.Contain("Message Monitor"));
            Assert.That(readme, Does.Contain("Flow Graph"));
            Assert.That(readme, Does.Contain("Inspector overlay"));
            Assert.That(readme, Does.Contain("Project Settings"));
            Assert.That(readme, Does.Contain("DxMessaging Guided Tour"));
            Assert.That(readme, Does.Contain("Reset Counters And Emit Burst"));
            Assert.That(readme, Does.Contain("history stays visible"));
            Assert.That(readme, Does.Contain("sample-pulse-001"));
            Assert.That(readme, Does.Contain("DiagnosticsToolingSampleContractTests"));
        }

        [Test]
        public void SampleGuideProvidesAnEndToEndEditorToolingTour()
        {
            string guide = ReadSampleFile(GuideScriptFileName);
            string runner = ReadSampleFile(RunnerScriptFileName);

            Assert.That(guide, Does.Contain("Diagnostics Tooling Guided Tour"));
            Assert.That(guide, Does.Contain("DxMessagingMessageMonitorWindow.Open"));
            Assert.That(guide, Does.Contain("DxMessagingFlowGraphWindow.Open"));
            Assert.That(guide, Does.Contain("Project/Wallstop Studios/DxMessaging"));
            Assert.That(guide, Does.Contain("Select All Receivers"));
            Assert.That(guide, Does.Contain("Reset Counters And Emit Burst"));
            Assert.That(guide, Does.Contain("trace IDs continue forward"));
            Assert.That(guide, Does.Contain("DxMessagingEditorTheme.ApplyWindow"));
            Assert.That(guide, Does.Contain("DiagnosticsToolingExerciser.unity"));
            Assert.That(guide, Does.Contain("SessionState.GetBool"));
            Assert.That(guide, Does.Contain("if (runner != null)"));
            Assert.That(runner, Does.Contain("public void ResetCountersAndEmitBurst()"));
            Assert.That(runner, Does.Not.Contain("sequence = 0;"));
        }

        [Test]
        public void ImportedSampleGuideBuildsInteractiveStateAndClaimsOnlyOneSampleScenePerSession()
        {
            Type? windowType = FindLoadedType(
                "WallstopStudios.DxMessagingSamples.DiagnosticsToolingExerciser.Editor.DiagnosticsToolingGuideWindow"
            );
            Type? bootstrapType = FindLoadedType(
                "WallstopStudios.DxMessagingSamples.DiagnosticsToolingExerciser.Editor.DiagnosticsToolingGuideBootstrap"
            );
            if (windowType == null || bootstrapType == null)
            {
                Assert.Ignore(
                    "The guide assembly is available when Unity imports the sample; the CI host copies every sample for this behavior check."
                );
                return;
            }

            const string sessionKey = "DxMessaging.DiagnosticsToolingGuide.Opened";
            bool previousSessionValue = SessionState.GetBool(sessionKey, false);
            EditorWindow? window = null;
            try
            {
                SessionState.SetBool(sessionKey, false);
                MethodInfo claim = bootstrapType.GetMethod(
                    "TryClaimSessionForScene",
                    BindingFlags.Static | BindingFlags.NonPublic
                )!;
                Assert.That(
                    claim.Invoke(
                        null,
                        new object[] { "Assets/Unrelated/DiagnosticsToolingExerciser.unity" }
                    ),
                    Is.False,
                    "A filename collision outside the sample folder must not open the guide."
                );
                const string importedScene =
                    "Assets/Samples/DxMessaging/1.0.0/Diagnostics Tooling Exerciser/DiagnosticsToolingExerciser.unity";
                Assert.That(claim.Invoke(null, new object[] { importedScene }), Is.True);
                Assert.That(
                    claim.Invoke(null, new object[] { importedScene }),
                    Is.False,
                    "The guide must auto-open only once per editor session."
                );

                window = ScriptableObject.CreateInstance(windowType) as EditorWindow;
                Assert.That(window, Is.Not.Null);
                MethodInfo createGui = windowType.GetMethod(
                    "CreateGUI",
                    BindingFlags.Instance | BindingFlags.NonPublic
                )!;
                createGui.Invoke(window, null);

                VisualElement root = window!.rootVisualElement;
                Label? status = root.Q<Label>("dx-tooling-guide-status");
                Button? play = root.Q<Button>("dx-tooling-guide-play");
                Button? emit = root.Q<Button>("dx-tooling-guide-emit");
                Button? reset = root.Q<Button>("dx-tooling-guide-reset");
                Assert.That(status, Is.Not.Null);
                Assert.That(play, Is.Not.Null.And.Property(nameof(VisualElement.enabledSelf)).True);
                Assert.That(
                    emit,
                    Is.Not.Null.And.Property(nameof(VisualElement.enabledSelf)).False
                );
                Assert.That(
                    reset,
                    Is.Not.Null.And.Property(nameof(VisualElement.enabledSelf)).False
                );

                FieldInfo scheduledRefresh = windowType.GetField(
                    "_statusRefresh",
                    BindingFlags.Instance | BindingFlags.NonPublic
                )!;
                Assert.That(scheduledRefresh.GetValue(window), Is.Not.Null);
            }
            finally
            {
                if (window != null)
                {
                    EditorWindowTestUtility.CloseWindow(window);
                }
                SessionState.SetBool(sessionKey, previousSessionValue);
            }
        }

        private static Type? FindLoadedType(string fullName)
        {
            return AppDomain
                .CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null);
        }

        private static string ReadSampleFile(string relativeFilePath)
        {
            return File.ReadAllText(
                Path.Combine(GetPackageRoot(), SampleRelativePath, relativeFilePath)
            );
        }

        private static string ReadPackageFile(string relativeFilePath)
        {
            return File.ReadAllText(Path.Combine(GetPackageRoot(), relativeFilePath));
        }

        private static string ReadGuid(string metaFileName)
        {
            string meta = ReadSampleFile(metaFileName);
            Match match = Regex.Match(meta, "^guid: ([0-9a-f]{32})$", RegexOptions.Multiline);
            Assert.That(match.Success, Is.True, $"Missing Unity guid in {metaFileName}.");
            return match.Groups[1].Value;
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static string GetPackageRoot()
        {
            return Path.GetFullPath(
                Path.Combine(
                    Application.dataPath,
                    "..",
                    "Packages",
                    "com.wallstop-studios.dxmessaging"
                )
            );
        }
    }
}
#endif
