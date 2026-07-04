#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System.IO;
    using System.Text.RegularExpressions;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class DiagnosticsToolingSampleContractTests
    {
        private const string SampleRelativePath = "Samples~/Diagnostics Tooling Exerciser";
        private const string SceneFileName = "DiagnosticsToolingExerciser.unity";
        private const string RunnerScriptFileName = "DiagnosticsToolingExerciser.cs";
        private const string ReceiverScriptFileName = "DiagnosticsToolingReceiver.cs";
        private const string MessagesScriptFileName = "Messages.cs";
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
            Assert.That(receiver, Does.Contain("EnsureToolingRegistrations()"));
            Assert.That(runner, Does.Contain("EnsureReceiversReady();"));
            Assert.That(receiver, Does.Contain("base.Awake();"));
            Assert.That(receiver, Does.Contain("base.OnEnable();"));
            Assert.That(receiver, Does.Contain("base.OnDisable();"));
        }

        [Test]
        public void SampleReadmeDocumentsToolVerificationWorkflow()
        {
            string readme = ReadSampleFile("README.md");

            Assert.That(readme, Does.Contain("Message Monitor"));
            Assert.That(readme, Does.Contain("Flow Graph"));
            Assert.That(readme, Does.Contain("Inspector overlay"));
            Assert.That(readme, Does.Contain("Project Settings"));
            Assert.That(readme, Does.Contain("sample-pulse-001"));
            Assert.That(readme, Does.Contain("DiagnosticsToolingSampleContractTests"));
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
