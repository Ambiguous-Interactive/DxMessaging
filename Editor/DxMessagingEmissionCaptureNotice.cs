#if UNITY_EDITOR
namespace DxMessaging.Editor
{
    using System;
    using Core.MessageBus;
    using DxMessaging.Editor.Settings;
    using UnityEditor;
    using UnityEngine.UIElements;

    /// <summary>
    /// One definition of how the editor surfaces explain a MISSING emission call site, and one
    /// place that turns capture back on.
    ///
    /// <para>
    /// Emission-site capture is opt-in (issue #433) because capturing a managed stack trace costs
    /// hundreds of microseconds and tens of allocations per diagnostic record. That is the right
    /// default, but it means the Message Monitor stack pane and the Flow Graph emission-site rows
    /// are empty for most users, and an empty pane that does not say WHY reads as a broken tool.
    /// Every surface that renders a call site routes its empty state through here so the reason and
    /// the fix are identical wherever the user happens to be looking.
    /// </para>
    /// </summary>
    internal static class DxMessagingEmissionCaptureNotice
    {
        /// <summary>Name of the enable button, so tests and surfaces can find it.</summary>
        internal const string EnableButtonName = "dxmessaging-enable-emission-capture";

        /// <summary>Short suffix for a collapsed header, e.g. "Stack trace (capture off)".</summary>
        internal const string DisabledSummary = "capture off";

        internal const string EnableButtonText = "Enable stack traces";

        internal const string DisabledExplanation =
            "Emission stack traces are off, so no call site was recorded. Capturing one walks the "
            + "managed stack on every diagnostic record, which is why it is opt-in. Turning it on "
            + "applies to emissions from here on; rows already recorded stay empty.";

        internal const string SettingsPathHint =
            "Project Settings > Wallstop Studios > DxMessaging > Capture Emission Stack Traces";

        /// <summary>
        /// Whether emission-site capture is currently recording call sites.
        /// </summary>
        internal static bool CaptureEnabled => IMessageBus.GlobalDiagnosticsStackTraces;

        /// <summary>
        /// Test seam mirroring the inspector overlay's: lets a test run the settings write inline
        /// instead of waiting on <c>delayCall</c> plus an editor-idle tick.
        /// </summary>
        internal static Action<Action> AssetDatabaseMutationScheduler { get; set; }

        /// <summary>
        /// Turns capture on for this session immediately, then persists it to the settings asset so
        /// it survives a domain reload. The in-memory flip is deliberately NOT deferred: the user
        /// clicked a button and expects the next emission to carry a site, while the asset write
        /// has to wait for an editor-idle tick like every other settings mutation.
        /// </summary>
        internal static void EnableCapture()
        {
            IMessageBus.GlobalDiagnosticsStackTraces = true;

            Action<Action> schedule =
                AssetDatabaseMutationScheduler
                ?? DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
            schedule(() =>
            {
                try
                {
                    DxMessagingSettings settings = DxMessagingSettings.GetOrCreateSettings();
                    if (settings == null)
                    {
                        return;
                    }

                    if (settings.DiagnosticsStackTraces)
                    {
                        return;
                    }

                    settings.DiagnosticsStackTraces = true;
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                }
                catch (Exception ex)
                {
                    DxMessagingEditorLog.LogWarning(
                        "Failed to persist the emission stack-trace capture setting.",
                        ex
                    );
                }
            });
        }

        /// <summary>
        /// Tooltip text for a surface that can only show a string: the captured call site when
        /// there is one, otherwise the reason it is missing and where the switch lives. Returns
        /// empty when capture is on and the record simply predates it, so an IMGUI tooltip does
        /// not assert something false.
        /// </summary>
        internal static string DescribeCallSiteTooltip(string stackTrace)
        {
            if (!string.IsNullOrWhiteSpace(stackTrace))
            {
                return stackTrace;
            }

            return CaptureEnabled
                ? string.Empty
                : $"{DisabledExplanation} Turn it on at {SettingsPathHint}.";
        }

        /// <summary>
        /// Builds the "capture is off, here is the switch" block: the reason, where the setting
        /// lives, and a one-click enable.
        /// </summary>
        internal static VisualElement CreateDisabledNotice(string explanationLabelName)
        {
            VisualElement notice = new();

            Label explanation = new(DisabledExplanation) { name = explanationLabelName };
            explanation.style.whiteSpace = WhiteSpace.Normal;
            notice.Add(explanation);

            Button enable = new(EnableCapture)
            {
                name = EnableButtonName,
                text = EnableButtonText,
                tooltip = SettingsPathHint,
            };
            enable.style.alignSelf = Align.FlexStart;
            enable.style.marginTop = 4;
            notice.Add(enable);

            return notice;
        }
    }
}
#endif
