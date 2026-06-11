namespace DxMessaging.Tests.Editor.Contract
{
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    /// <summary>
    /// Pins the documented <see cref="DiagnosticsTarget"/> gating semantics:
    /// <see cref="DiagnosticsTarget.Editor"/> enables diagnostics while in the Unity editor,
    /// <see cref="DiagnosticsTarget.Runtime"/> enables diagnostics in player/runtime builds,
    /// and <see cref="DiagnosticsTarget.All"/> enables both. The full flag-by-environment
    /// matrix is asserted so any drift in <see cref="IMessageBus.ShouldEnableDiagnostics(DiagnosticsTarget, bool)"/>
    /// fails with a focused message.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class DiagnosticsTargetGatingTests
    {
        [Test]
        public void OffDisablesDiagnosticsEverywhere()
        {
            Assert.IsFalse(
                IMessageBus.ShouldEnableDiagnostics(DiagnosticsTarget.Off, isEditor: true),
                "Off must disable diagnostics in the editor."
            );
            Assert.IsFalse(
                IMessageBus.ShouldEnableDiagnostics(DiagnosticsTarget.Off, isEditor: false),
                "Off must disable diagnostics in player builds."
            );
        }

        [Test]
        public void EditorFlagEnablesOnlyInEditor()
        {
            Assert.IsTrue(
                IMessageBus.ShouldEnableDiagnostics(DiagnosticsTarget.Editor, isEditor: true),
                "Editor flag must enable diagnostics while in the Unity editor."
            );
            Assert.IsFalse(
                IMessageBus.ShouldEnableDiagnostics(DiagnosticsTarget.Editor, isEditor: false),
                "Editor flag must not enable diagnostics in player builds."
            );
        }

        [Test]
        public void RuntimeFlagEnablesOnlyInPlayerBuilds()
        {
            Assert.IsFalse(
                IMessageBus.ShouldEnableDiagnostics(DiagnosticsTarget.Runtime, isEditor: true),
                "Runtime flag targets player/runtime builds; the editor requires the Editor flag."
            );
            Assert.IsTrue(
                IMessageBus.ShouldEnableDiagnostics(DiagnosticsTarget.Runtime, isEditor: false),
                "Runtime flag must enable diagnostics in player builds."
            );
        }

        [Test]
        public void AllEnablesDiagnosticsEverywhere()
        {
            Assert.IsTrue(
                IMessageBus.ShouldEnableDiagnostics(DiagnosticsTarget.All, isEditor: true),
                "All must enable diagnostics in the editor."
            );
            Assert.IsTrue(
                IMessageBus.ShouldEnableDiagnostics(DiagnosticsTarget.All, isEditor: false),
                "All must enable diagnostics in player builds."
            );
        }

        [Test]
        public void ParameterlessOverloadHonorsEditorEnvironment()
        {
            DiagnosticsTarget previous = IMessageBus.GlobalDiagnosticsTargets;
            try
            {
                IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Editor;
                Assert.IsTrue(
                    IMessageBus.ShouldEnableDiagnostics(),
                    "Editor flag must enable diagnostics when running inside the Unity editor."
                );
                IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
                Assert.IsFalse(
                    IMessageBus.ShouldEnableDiagnostics(),
                    "Off must disable diagnostics when running inside the Unity editor."
                );
            }
            finally
            {
                IMessageBus.GlobalDiagnosticsTargets = previous;
            }
        }
    }
}
