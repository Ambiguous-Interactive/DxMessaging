#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Core.MessageBus;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Windows;
    using NUnit.Framework;
    using UnityEngine.UIElements;

    /// <summary>
    /// Emission-site capture is opt-in (issue #433), which means the Message Monitor stack pane and
    /// the Flow Graph emission-site rows are empty for most users. An empty pane that does not say
    /// WHY reads as a broken tool, so these tests pin the three things that keep it honest: the
    /// empty state names the setting, it carries a one-click switch, and neither claim is made when
    /// capture is already on.
    /// </summary>
    [TestFixture]
    public sealed class DxMessagingEmissionCaptureNoticeTests
    {
        private bool _captureBeforeTest;

        [SetUp]
        public void SetUp()
        {
            _captureBeforeTest = IMessageBus.GlobalDiagnosticsStackTraces;
        }

        [TearDown]
        public void TearDown()
        {
            DxMessagingEmissionCaptureNotice.AssetDatabaseMutationScheduler = null;
            IMessageBus.GlobalDiagnosticsStackTraces = _captureBeforeTest;
        }

        [Test]
        public void MonitorStackPaneNamesTheSettingAndCarriesItsSwitchWhenCaptureIsOff()
        {
            IMessageBus.GlobalDiagnosticsStackTraces = false;

            VisualElement section = DxMessagingMessageMonitorWindow.CreateStackTraceSection(
                new MessageMonitorEntry("Sample.Message", "Context: Player", string.Empty),
                DxMessagingMessageMonitorWindow.DetailsStackFoldoutName,
                DxMessagingMessageMonitorWindow.DetailsStackFirstFrameLabelName
            );

            Foldout foldout = section as Foldout;
            Assert.That(foldout, Is.Not.Null);
            Assert.That(
                foldout.text,
                Does.Contain(DxMessagingEmissionCaptureNotice.DisabledSummary),
                "The collapsed header must say the capture setting is off, not just that a trace "
                    + "is absent."
            );
            Assert.That(
                foldout.value,
                Is.True,
                "The capture-off state must start expanded; a collapsed disclosure is exactly how "
                    + "the setting stays invisible."
            );

            Label explanation = foldout.Q<Label>(
                DxMessagingMessageMonitorWindow.DetailsStackFirstFrameLabelName
            );
            Assert.That(explanation, Is.Not.Null);
            Assert.That(
                explanation.text,
                Is.EqualTo(DxMessagingEmissionCaptureNotice.DisabledExplanation)
            );

            Button enable = foldout.Q<Button>(DxMessagingEmissionCaptureNotice.EnableButtonName);
            Assert.That(enable, Is.Not.Null, "The explanation must travel with the switch.");
            Assert.That(enable.text, Is.EqualTo(DxMessagingEmissionCaptureNotice.EnableButtonText));
            Assert.That(
                enable.tooltip,
                Does.Contain(DxMessagingEmissionCaptureNotice.SettingsPathHint),
                "The tooltip must say where the setting lives so it can be turned back off."
            );
        }

        [Test]
        public void MonitorStackPaneDoesNotBlameTheSettingWhenCaptureIsOn()
        {
            IMessageBus.GlobalDiagnosticsStackTraces = true;

            VisualElement section = DxMessagingMessageMonitorWindow.CreateStackTraceSection(
                new MessageMonitorEntry("Sample.Message", "Context: Player", string.Empty),
                DxMessagingMessageMonitorWindow.DetailsStackFoldoutName,
                DxMessagingMessageMonitorWindow.DetailsStackFirstFrameLabelName
            );

            Foldout foldout = section as Foldout;
            Assert.That(foldout, Is.Not.Null);
            Assert.That(
                foldout.text,
                Does.Not.Contain(DxMessagingEmissionCaptureNotice.DisabledSummary),
                "With capture on, an empty trace is a record written before it was enabled, not "
                    + "the setting."
            );
            Assert.That(foldout.value, Is.False, "Only the capture-off state opens by default.");
            Assert.That(
                foldout.Q<Button>(DxMessagingEmissionCaptureNotice.EnableButtonName),
                Is.Null,
                "Offering to enable a setting that is already on would be a false affordance."
            );
        }

        [Test]
        public void CapturedTraceWithOnlyEngineFramesIsNotBlamedOnTheSetting()
        {
            IMessageBus.GlobalDiagnosticsStackTraces = false;

            VisualElement section = DxMessagingMessageMonitorWindow.CreateStackTraceSection(
                new MessageMonitorEntry(
                    "Sample.Message",
                    "Context: Player",
                    "UnityEngine.StackTraceUtility:ExtractStackTrace ()"
                ),
                DxMessagingMessageMonitorWindow.DetailsStackFoldoutName,
                DxMessagingMessageMonitorWindow.DetailsStackFirstFrameLabelName
            );

            Foldout foldout = section as Foldout;
            Assert.That(foldout, Is.Not.Null);
            Assert.That(
                foldout.text,
                Does.Not.Contain(DxMessagingEmissionCaptureNotice.DisabledSummary),
                "A trace that WAS captured but holds only engine frames is a different fact from "
                    + "one the setting suppressed, even while the setting is off."
            );
            Assert.That(
                foldout.Q<Button>(DxMessagingEmissionCaptureNotice.EnableButtonName),
                Is.Null
            );
        }

        [Test]
        public void FlowGraphEmissionSiteRowsNameTheSettingWhenCaptureIsOff(
            [Values(true, false)] bool captureEnabled
        )
        {
            IMessageBus.GlobalDiagnosticsStackTraces = captureEnabled;
            VisualElement section = new();

            // The list a node that HAS emitted actually carries while capture is off: one
            // placeholder per emission, never an empty list. Passing Array.Empty here would have
            // let the notice pass a test it could not pass in the window (Bugbot, PR #434).
            DxMessagingFlowGraphWindow.AddSourceDetailValues(
                section,
                "Emitted by",
                new[]
                {
                    DxMessagingEditorSourceLinks.UnknownCallSite,
                    DxMessagingEditorSourceLinks.UnknownCallSite,
                }
            );

            List<Label> labels = section.Query<Label>().ToList();
            bool namesTheSetting = labels.Exists(label =>
                label.text.Contains(
                    DxMessagingEmissionCaptureNotice.DisabledSummary,
                    StringComparison.Ordinal
                )
            );
            Button enable = section.Q<Button>(DxMessagingEmissionCaptureNotice.EnableButtonName);

            if (captureEnabled)
            {
                Assert.That(
                    namesTheSetting,
                    Is.False,
                    "With capture on, no emission sites means none were observed, which is what "
                        + "the plain wording already says."
                );
                Assert.That(enable, Is.Null);
            }
            else
            {
                Assert.That(
                    namesTheSetting,
                    Is.True,
                    "\"none captured\" alone reads as \"we looked and found nothing\", which is "
                        + "wrong when the setting suppressed the capture."
                );
                Assert.That(enable, Is.Not.Null, "The explanation must travel with the switch.");
            }
        }

        [Test]
        public void FlowGraphKeepsRealCallSitesAndDropsPlaceholdersWithoutBlamingTheSetting()
        {
            IMessageBus.GlobalDiagnosticsStackTraces = false;
            VisualElement section = new();

            DxMessagingFlowGraphWindow.AddSourceDetailValues(
                section,
                "Emitted by",
                new[]
                {
                    DxMessagingEditorSourceLinks.UnknownCallSite,
                    "Sample:Emit () (at Assets/Sample.cs:12)",
                }
            );

            List<Label> labels = section.Query<Label>().ToList();
            Assert.That(
                labels.Exists(label => label.text.Contains("Sample", StringComparison.Ordinal)),
                Is.True,
                "A real call site must survive alongside placeholders."
            );
            Assert.That(
                labels.Exists(label =>
                    label.text.Contains(
                        DxMessagingEditorSourceLinks.UnknownCallSite,
                        StringComparison.Ordinal
                    )
                ),
                Is.False,
                "A placeholder row says nothing; rendering it only buries the real site."
            );
            Assert.That(
                section.Q<Button>(DxMessagingEmissionCaptureNotice.EnableButtonName),
                Is.Null,
                "Some sites WERE recorded, so the capture-off notice would be misleading here."
            );
        }

        [Test]
        public void EnableCaptureTakesEffectImmediatelyAndPersistsToSettings()
        {
            IMessageBus.GlobalDiagnosticsStackTraces = false;
            // The scheduled body is captured but deliberately NOT run: it calls
            // DxMessagingSettings.GetOrCreateSettings(), which would create or rewrite the real
            // project settings asset in whichever editor runs this suite. What is testable here
            // without that side effect is the contract that matters -- immediate in-memory effect,
            // deferred durable write.
            List<Action> scheduled = new();
            DxMessagingEmissionCaptureNotice.AssetDatabaseMutationScheduler = scheduled.Add;

            DxMessagingEmissionCaptureNotice.EnableCapture();

            Assert.That(
                IMessageBus.GlobalDiagnosticsStackTraces,
                Is.True,
                "The in-memory flip must not wait on an editor-idle tick; the user clicked a "
                    + "button and the next emission has to carry a site."
            );
            Assert.That(
                scheduled.Count,
                Is.EqualTo(1),
                "The durable settings write must be deferred like every other settings mutation."
            );
        }

        [Test]
        public void CallSiteTooltipExplainsTheSettingOnlyWhenItIsTheReason(
            [Values(true, false)] bool captureEnabled
        )
        {
            IMessageBus.GlobalDiagnosticsStackTraces = captureEnabled;

            string captured = DxMessagingEmissionCaptureNotice.DescribeCallSiteTooltip(
                "Sample:Emit () (at Assets/Sample.cs:12)"
            );
            string missing = DxMessagingEmissionCaptureNotice.DescribeCallSiteTooltip(string.Empty);

            Assert.That(
                captured,
                Is.EqualTo("Sample:Emit () (at Assets/Sample.cs:12)"),
                "A real call site is always shown verbatim."
            );
            if (captureEnabled)
            {
                Assert.That(
                    missing,
                    Is.Empty,
                    "With capture on, a missing site is not the setting's doing, so the tooltip "
                        + "must not assert that it is."
                );
            }
            else
            {
                Assert.That(
                    missing,
                    Does.Contain(DxMessagingEmissionCaptureNotice.SettingsPathHint)
                );
            }
        }
    }
}
#endif
