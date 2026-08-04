#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Core;
    using Core.Diagnostics;
    using Core.MessageBus;
    using Core.Messages;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Windows;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEditor.SceneManagement;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    [TestFixture]
    public sealed class DxMessagingMessageMonitorWindowTests
    {
        private readonly List<Object> _createdObjects = new();
        private readonly List<string> _createdAssetPaths = new();
        private readonly List<EditorWindow> _createdWindows = new();
        private const string MessageTypeLanesName = "dxmessaging-monitor-message-type-lanes";
        private const string MessageTypeLaneScrollViewName =
            "dxmessaging-monitor-message-type-lane-scroll";
        private const string MessageTypeLaneRowClassName =
            "dxmessaging-monitor-message-type-lane-row";
        private const string MessageTypeLanesSummaryLabelName =
            "dxmessaging-monitor-message-type-lanes-summary";
        private const string MessageTypeLaneTypeLabelName =
            "dxmessaging-monitor-message-type-lane-type";
        private const string MessageTypeLaneSummaryLabelName =
            "dxmessaging-monitor-message-type-lane-summary";
        private const string MessageTypeLaneFilterButtonName =
            "dxmessaging-monitor-message-type-lane-filter";
        private const string ContextLanesName = "dxmessaging-monitor-context-lanes";
        private const string ContextLaneScrollViewName = "dxmessaging-monitor-context-lane-scroll";
        private const string ContextLaneRowClassName = "dxmessaging-monitor-context-lane-row";
        private const string ContextLanesSummaryLabelName =
            "dxmessaging-monitor-context-lanes-summary";
        private const string ContextLaneContextLabelName =
            "dxmessaging-monitor-context-lane-context";
        private const string ContextLaneSummaryLabelName =
            "dxmessaging-monitor-context-lane-summary";
        private const string ContextLaneFilterButtonName =
            "dxmessaging-monitor-context-lane-filter";
        private const string ActiveFilterSummaryName = "dxmessaging-monitor-active-filter";
        private const string ActiveFilterSummaryLabelName =
            "dxmessaging-monitor-active-filter-label";
        private const string ActiveFilterTokenScrollViewName =
            "dxmessaging-monitor-active-filter-token-scroll";
        private const string ActiveFilterTokenClassName = "dxmessaging-monitor-active-filter-token";
        private const string ActiveFilterClearButtonName =
            "dxmessaging-monitor-active-filter-clear";

        [TearDown]
        public void TearDown()
        {
            // Windows close in a finally: object and asset cleanup below runs Unity code that can
            // throw, and a leaked EditorWindow outlives the test that made it. It stays subscribed
            // to statics (this fixture's window subscribes to the shared source index), keeps a
            // panel alive, and turns one failing test into a cascade in whatever runs next.
            try
            {
                foreach (Object instance in _createdObjects)
                {
                    if (instance != null)
                    {
                        if (instance is GameObject gameObject)
                        {
                            foreach (
                                MessagingComponent messagingComponent in gameObject.GetComponentsInChildren<MessagingComponent>(
                                    includeInactive: true
                                )
                            )
                            {
                                messagingComponent.EditorResetRuntimeState();
                            }
                        }

                        Object.DestroyImmediate(instance);
                    }
                }
                _createdObjects.Clear();

                foreach (string assetPath in _createdAssetPaths)
                {
                    if (!string.IsNullOrWhiteSpace(assetPath))
                    {
                        EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                            AssetDatabase.DeleteAsset(assetPath)
                        );
                    }
                }
                _createdAssetPaths.Clear();
            }
            finally
            {
                // Closing a shown window under -nographics logs a benign "No graphic device is
                // available" error, and Unity resets LogAssert tolerance per phase -- so the
                // tolerance ShowWindow asserted in the test body does not reach teardown. Any
                // test here that holds a shown window open until now fails without this.
                // Headless only, so runs with a real GPU keep full strictness (which is also why
                // this cannot reproduce on a developer machine).
                EditorWindowTestUtility.SuppressHeadlessWindowRenderErrors();
                EditorWindowTestUtility.CloseTrackedWindows(_createdWindows);
            }

            if (MessageHandler.MessageBus is MessageBus messageBus)
            {
                messageBus.DiagnosticsMode = false;
                messageBus._emissionBuffer.Clear();
            }
        }

        [Test]
        public void MessageMonitorDoesNotUseFocusedInspectorTickRefresh()
        {
            Assert.That(
                typeof(DxMessagingMessageMonitorWindow).GetMethod(
                    "OnInspectorUpdate",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
                ),
                Is.Null
            );
        }

        [Test]
        public void BuildMonitorUiRendersDisabledState()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: false,
                capacity: IMessageBus.DefaultMessageBufferSize,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                MessageMonitorViewState.Default,
                onCopyExport: _ => { }
            );

            Assert.That(
                root.ClassListContains(DxMessagingMessageMonitorWindow.RootClassName),
                Is.True
            );
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.ThemeClassName), Is.True);
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.WindowClassName), Is.True);
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.ToolbarClassName
                    )
                    .First()
                    .ClassListContains(DxMessagingEditorTheme.ToolbarClassName),
                Is.True
            );
            Assert.That(
                root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName)
                    .ClassListContains(DxMessagingEditorTheme.ToolButtonClassName),
                Is.True
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                Does.Contain("Off")
            );
            Assert.That(
                root.Query<VisualElement>(className: DxMessagingMessageMonitorWindow.RowClassName)
                    .ToList(),
                Is.Empty
            );
            Label emptyBody = root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName);
            Assert.That(emptyBody, Is.Not.Null);
            Assert.That(emptyBody.text, Does.Contain("Enable"));
            Assert.That(
                emptyBody.ClassListContains(DxMessagingEditorTheme.EmptyBodyClassName),
                Is.True
            );
            Assert.That(
                emptyBody.parent.ClassListContains(DxMessagingEditorTheme.EmptyClassName),
                Is.True
            );
            Label emptyTitle = root.Q<Label>(
                DxMessagingMessageMonitorWindow.EmptyStateTitleLabelName
            );
            Assert.That(emptyTitle, Is.Not.Null);
            Assert.That(
                emptyTitle.ClassListContains(DxMessagingEditorTheme.EmptyTitleClassName),
                Is.True
            );
            Assert.That(
                root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName).enabledSelf,
                Is.False
            );
            Assert.That(
                DxMessagingMessageMonitorWindow.CreateExportText(snapshot, snapshot.Entries),
                Does.Contain("\"entryCount\": 0")
            );
        }

        // The snapshot is built inside the test body (not passed as a parameter) because
        // MessageMonitorSnapshot is internal, and a public [Test] method may not expose an
        // internal parameter type (CS0051).
        [TestCase("unavailable", "Monitor unavailable", "active global bus")]
        [TestCase("diagnostics-off", "Diagnostics are Off", "Enable diagnostics")]
        [TestCase("no-messages-yet", "No messages yet", "recorded")]
        public void BuildMonitorUiEmptyStateHasExpectedTitleAndBody(
            string state,
            string expectedTitle,
            string expectedBodySubstring
        )
        {
            MessageMonitorSnapshot snapshot = state switch
            {
                "unavailable" => MessageMonitorSnapshot.Unavailable(
                    "The active global bus is not the default DxMessaging MessageBus."
                ),
                "diagnostics-off" => new MessageMonitorSnapshot(
                    diagnosticsEnabled: false,
                    capacity: 8,
                    entries: Array.Empty<MessageMonitorEntry>()
                ),
                _ => new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 8,
                    entries: Array.Empty<MessageMonitorEntry>()
                ),
            };
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            Label title = root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateTitleLabelName);
            Assert.That(title, Is.Not.Null);
            Assert.That(title.text, Is.EqualTo(expectedTitle));
            Assert.That(
                title.ClassListContains(DxMessagingEditorTheme.EmptyTitleClassName),
                Is.True
            );

            Label body = root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName);
            Assert.That(body, Is.Not.Null);
            Assert.That(body.text, Does.Contain(expectedBodySubstring));
            Assert.That(body.ClassListContains(DxMessagingEditorTheme.EmptyBodyClassName), Is.True);
            Assert.That(
                body.parent.ClassListContains(DxMessagingEditorTheme.EmptyClassName),
                Is.True
            );
        }

        [Test]
        public void BuildMonitorUiRendersMostRecentEntriesFirst()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                Does.Contain("123")
            );
            Assert.That(
                rows[1].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(OlderMessage))
            );
        }

        [Test]
        public void BuildMonitorUiRendersTaxonomyChipForKnownMessageKinds()
        {
            MessageMonitorEntry untargeted = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry targeted = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorEntry broadcast = CreateEntry(
                new BroadcastMessage(),
                new InstanceId(456)
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { untargeted, targeted, broadcast }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            Dictionary<string, VisualElement> rowsByType = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList()
                .ToDictionary(
                    row => row.Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                    StringComparer.Ordinal
                );

            AssertTaxonomyRow(rowsByType[nameof(OlderMessage)], "Untargeted");
            AssertTaxonomyRow(rowsByType[nameof(NewerMessage)], "Targeted");
            AssertTaxonomyRow(rowsByType[nameof(BroadcastMessage)], "Broadcast");
        }

        [Test]
        public void BuildMonitorUiKeepsStackTracesOutOfLogRows()
        {
            MessageMonitorEntry entry = new(
                nameof(OlderMessage),
                "Context: Player",
                CapturedStackTrace
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Query<Label>().ToList().ConvertAll(label => label.text),
                Has.None.Contains("ExtractStackTraceNoAlloc"),
                "A log row must not render the stack trace; it belongs to the selected entry."
            );

            Foldout stack = root.Q<Foldout>(
                DxMessagingMessageMonitorWindow.DetailsStackFoldoutName
            );
            Assert.That(stack, Is.Not.Null);
            Assert.That(stack.value, Is.False, "The stack trace disclosure must start collapsed.");
            Assert.That(
                stack
                    .Q<Label>(DxMessagingMessageMonitorWindow.DetailsStackFirstFrameLabelName)
                    .text,
                Does.Contain("EmitOneOfEach"),
                "The first frame shown must be the emitting call site."
            );
        }

        /// <summary>
        /// What <see cref="MessageEmissionData"/> actually hands the Monitor: Unity's own two
        /// stack-capture frames on top, then the frames that describe the emitting code. Issue
        /// #344 reported the capture frames as noise, so tests use a trace that has both rather
        /// than one that is only the noise.
        /// </summary>
        private const string CapturedStackTrace =
            "UnityEngine.Debug:ExtractStackTraceNoAlloc (byte*,int,string)\n"
            + "UnityEngine.StackTraceUtility:ExtractStackTrace ()\n"
            + "WallstopStudios.Sample.Exerciser:EmitOneOfEach () (at Assets/Sample/Exerciser.cs:185)\n"
            + "WallstopStudios.Sample.Exerciser:EmitBurst () (at Assets/Sample/Exerciser.cs:138)";

        /// <summary>
        /// Issue #344's second round circled the left edge of the log and wrote "PADDING". The
        /// gutter was pinned to the time column, which only live mode renders, so snapshot rows
        /// began at the window edge. Measured on the real laid-out window rather than asserted
        /// against the stylesheet, and measured for the header too, because a row that is
        /// indented under a header that is not reads as broken in the other direction.
        /// </summary>
        [Test]
        public void TheLogRowsAndHeaderShareAGutterAwayFromTheWindowEdge()
        {
            MessageMonitorEntry[] entries = Enumerable
                .Range(0, 6)
                .Select(index => new MessageMonitorEntry(
                    $"Sample.Message{index:00}",
                    $"Context: Object {index:00} (9748{index:00})",
                    CapturedStackTrace
                ))
                .ToArray();
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                window.position = new Rect(0f, 0f, 900f, 620f);
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                root.style.width = 900f;
                root.style.height = 620f;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    new MessageMonitorSnapshot(
                        diagnosticsEnabled: true,
                        capacity: 100,
                        entries: entries
                    )
                );

                Assert.That(root.panel, Is.Not.Null, "A shown window must produce a panel.");
                EditorSurfaceCapture.InvokeInheritedPanelMethod(
                    root.panel,
                    "ValidateLayout",
                    Array.Empty<object>()
                );

                VisualElement header = root.Q<VisualElement>(
                    DxMessagingMessageMonitorWindow.ListHeaderName
                );
                Assert.That(header, Is.Not.Null);
                VisualElement row = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .First();
                Assert.That(row, Is.Not.Null);

                AssertGutter(header, "the column header");
                AssertGutter(row, "a log row");
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        private static void AssertGutter(VisualElement container, string description)
        {
            VisualElement firstChild = container.Children().First();
            float inset = firstChild.worldBound.x - container.worldBound.x;
            Assert.That(
                inset,
                Is.GreaterThanOrEqualTo(12f),
                $"The leftmost content of {description} sits {inset}px from its left edge, so it "
                    + "reads as flush against the window."
            );
        }

        /// <summary>
        /// Issue #344's second round: "The 'Stacktrace' includes 'extract stack trace' rows."
        /// The trace becomes one row per frame that describes the emitting code, the capture
        /// frames are gone, and the first surviving frame is the emitting call site.
        /// </summary>
        [Test]
        public void TheStackTraceRendersOneRowPerFrameWithoutEngineCaptureFrames()
        {
            MessageMonitorEntry entry = new(
                nameof(OlderMessage),
                "Context: Player",
                CapturedStackTrace
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 8,
                    entries: new[] { entry }
                )
            );

            Foldout stack = root.Q<Foldout>(
                DxMessagingMessageMonitorWindow.DetailsStackFoldoutName
            );
            Assert.That(stack, Is.Not.Null);

            List<VisualElement> frameRows = stack
                .Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.DetailsStackFrameRowClassName
                )
                .ToList();
            Assert.That(
                frameRows.Count,
                Is.EqualTo(2),
                "The trace has four frames, two of which are Unity's own capture frames."
            );
            Assert.That(
                stack.Query<Label>().ToList().ConvertAll(label => label.text),
                Has.None.Contains("ExtractStackTrace"),
                "Unity's stack-capture frames describe taking the stack, never the emitting code."
            );
            Assert.That(
                stack
                    .Q<Label>(DxMessagingMessageMonitorWindow.DetailsStackFirstFrameLabelName)
                    .text,
                Does.Contain("EmitOneOfEach"),
                "The first surviving frame is the emitting call site and reads as the answer."
            );
            Assert.That(
                stack.text,
                Does.Contain("2"),
                "The disclosure header counts the frames a reader will actually see."
            );
        }

        /// <summary>
        /// Issue #344's second round: "We need to ideally be able to link/click the contexts."
        /// A context whose object is still alive selects and pings it; one whose object is gone
        /// -- the normal case for a log that outlives its scene -- stays readable but inert,
        /// rather than offering a link that would do nothing.
        /// </summary>
        /// <remarks>
        /// `Destroyed` is the case that actually matters and the one an id of 0 does not cover:
        /// 0 short-circuits before the object lookup, so it only proves "no context was
        /// captured". A real id whose object has been destroyed is what a log outliving its
        /// scene holds.
        /// </remarks>
        [TestCase(ContextState.Alive)]
        [TestCase(ContextState.Destroyed)]
        [TestCase(ContextState.NeverCaptured)]
        public void AContextLinksToItsObjectOnlyWhileThatObjectStillExists(ContextState state)
        {
            GameObject contextObject = new(
                nameof(AContextLinksToItsObjectOnlyWhileThatObjectStillExists)
            );
            int contextInstanceId = contextObject.GetInstanceID();
            if (state == ContextState.Destroyed)
            {
                Object.DestroyImmediate(contextObject);
            }
            else
            {
                _createdObjects.Add(contextObject);
            }

            bool contextIsAlive = state == ContextState.Alive;
            MessageMonitorEntry entry = new(
                nameof(OlderMessage),
                "Context: Player",
                CapturedStackTrace,
                contextInstanceId: state == ContextState.NeverCaptured ? 0 : contextInstanceId
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 8,
                    entries: new[] { entry }
                )
            );

            VisualElement contextRow = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.DetailsContextRowName
            );
            Assert.That(contextRow, Is.Not.Null, "The detail pane always names the context.");
            Label contextValue = contextRow.Q<Label>(
                DxMessagingMessageMonitorWindow.DetailsContextLabelName
            );
            Assert.That(contextValue, Is.Not.Null);
            Assert.That(
                contextValue.text,
                Is.EqualTo("Context: Player"),
                "An inert context is still readable."
            );
            Assert.That(
                contextValue.ClassListContains(DxMessagingEditorTheme.ClickableClassName),
                Is.EqualTo(contextIsAlive),
                contextIsAlive
                    ? "A live context must say it can be clicked."
                    : "A context whose object is gone must not offer a link that does nothing."
            );
            Assert.That(
                contextValue.focusable,
                Is.EqualTo(contextIsAlive),
                "Whatever a mouse can reach, a keyboard must reach too."
            );
        }

        /// <summary>
        /// Issue #344's second round: "The Component Diagnostics and other areas are not
        /// resizable." Each capped panel now carries a drag handle, and a drag past the shipped
        /// cap actually takes effect -- a `max-height` left in place would silently win.
        /// </summary>
        [Test]
        public void CappedPanelsCarryAResizeHandleThatCanGrowThemPastTheirShippedCap()
        {
            // Pointer capture needs a real panel, so this drives the shown window.
            EditorWindow window = CreateTrackedEditorWindow();
            try
            {
                window.position = new Rect(0f, 0f, 900f, 620f);
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                root.style.width = 900f;
                root.style.height = 620f;

                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    new MessageMonitorSnapshot(
                        diagnosticsEnabled: true,
                        capacity: 8,
                        entries: new[]
                        {
                            new MessageMonitorEntry(
                                nameof(OlderMessage),
                                "Context: Player",
                                CapturedStackTrace
                            ),
                        }
                    ),
                    MessageMonitorViewState.Default,
                    onRefresh: () => { },
                    onCopyExport: _ => { },
                    componentEntries: CreateComponentEntries(4)
                );

                VisualElement componentResizer = root.Q<VisualElement>(
                    DxMessagingMessageMonitorWindow.ComponentResizerName
                );
                Assert.That(
                    componentResizer,
                    Is.Not.Null,
                    "Component Diagnostics is the panel #344 named; it must be resizable."
                );
                VisualElement stackResizer = root.Q<VisualElement>(
                    DxMessagingMessageMonitorWindow.DetailsStackResizerName
                );
                Assert.That(stackResizer, Is.Not.Null, "The stack trace is capped too.");

                ScrollView componentScroll = root.Q<ScrollView>(
                    DxMessagingMessageMonitorWindow.ComponentScrollViewName
                );
                Assert.That(componentScroll, Is.Not.Null);
                Assert.That(
                    componentScroll.style.maxHeight.value.value,
                    Is.EqualTo(180f),
                    "The shipped cap is what makes the panel feel stuck."
                );

                // Lay the window out first: `worldBound` is NaN until it has, and a drag built from
                // NaN coordinates produces a NaN delta that clamps to NaN.
                EditorSurfaceCapture.InvokeInheritedPanelMethod(
                    root.panel,
                    "ValidateLayout",
                    Array.Empty<object>()
                );

                // Drive the handle's own pointer handlers rather than calling the apply helper
                // directly: a test that writes the height itself and reads it back would pass with
                // every callback in CreateResizeHandle deleted.
                DragResizeHandle(componentResizer, deltaY: 220f);

                Assert.That(
                    componentScroll.style.height.value.value,
                    Is.GreaterThan(180f),
                    "Dragging down must grow the panel past the shipped cap."
                );
                Assert.That(
                    componentScroll.style.maxHeight.value.value,
                    Is.GreaterThanOrEqualTo(componentScroll.style.height.value.value),
                    "A cap left below the dragged height would silently undo the drag."
                );
                Assert.That(
                    componentScroll.style.flexShrink.value,
                    Is.EqualTo(0f),
                    "A shrinkable target treats a height as a starting size Yoga takes back, so the "
                        + "drag would not survive layout."
                );

                // The dragged height has to survive the rebuild every filter keystroke causes, for
                // the same reason the disclosures remember whether they were open.
                float dragged = componentScroll.style.height.value.value;
                TextField filter = root.Q<TextField>(
                    DxMessagingMessageMonitorWindow.FilterFieldName
                );
                Assert.That(filter, Is.Not.Null);
                filter.value = "Sample";

                ScrollView rebuiltScroll = root.Q<ScrollView>(
                    DxMessagingMessageMonitorWindow.ComponentScrollViewName
                );
                Assert.That(rebuiltScroll, Is.Not.Null);
                Assert.That(
                    rebuiltScroll.style.height.value.value,
                    Is.EqualTo(dragged),
                    "A filter keystroke rebuilds this section; the height a reader dragged must come "
                        + "back with it."
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        /// <summary>
        /// Drives a resize handle through a real pointer-down / move / up sequence so the
        /// handle's own callbacks, capture, and clamping are what the assertions measure.
        /// </summary>
        private static void DragResizeHandle(VisualElement handle, float deltaY)
        {
            Vector2 start = new(handle.worldBound.center.x, handle.worldBound.center.y);
            using (
                PointerDownEvent down = PointerDownEvent.GetPooled(
                    new Event
                    {
                        type = EventType.MouseDown,
                        mousePosition = start,
                        button = 0,
                    }
                )
            )
            {
                down.target = handle;
                handle.SendEvent(down);
            }
            using (
                PointerMoveEvent move = PointerMoveEvent.GetPooled(
                    new Event
                    {
                        type = EventType.MouseDrag,
                        mousePosition = new Vector2(start.x, start.y + deltaY),
                        button = 0,
                    }
                )
            )
            {
                move.target = handle;
                handle.SendEvent(move);
            }
            using (
                PointerUpEvent up = PointerUpEvent.GetPooled(
                    new Event
                    {
                        type = EventType.MouseUp,
                        mousePosition = new Vector2(start.x, start.y + deltaY),
                        button = 0,
                    }
                )
            )
            {
                up.target = handle;
                handle.SendEvent(up);
            }
        }

        /// <summary>
        /// Issue #344's second round: "We can't go 'Un-Live' once live." The badge that names
        /// the mode is the control that changes it, in both modes, so a reader who finds the
        /// switch once finds it again.
        /// </summary>
        [Test]
        public void TheModeBadgeSwitchesModesSoNeitherModeIsOneWay()
        {
            int enterLiveCount = 0;
            // A click needs a panel to dispatch through, so this drives the real window rather
            // than a detached element.
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    window.rootVisualElement,
                    new MessageMonitorSnapshot(
                        diagnosticsEnabled: true,
                        capacity: 8,
                        entries: Array.Empty<MessageMonitorEntry>()
                    ),
                    MessageMonitorViewState.Default,
                    onRefresh: () => { },
                    onCopyExport: _ => { },
                    componentEntries: Array.Empty<ComponentMonitorEntry>(),
                    onEnterLiveMode: () => enterLiveCount++
                );

                Label badge = window.rootVisualElement.Q<Label>(
                    DxMessagingMessageMonitorWindow.ModeBadgeLabelName
                );
                Assert.That(badge, Is.Not.Null);
                Assert.That(
                    badge.ClassListContains(DxMessagingEditorTheme.ClickableClassName),
                    Is.True,
                    "The word that names the mode must say it can be clicked."
                );
                Assert.That(
                    badge.focusable,
                    Is.True,
                    "Whatever a mouse can reach, a keyboard must reach too."
                );

                SendClick(badge);

                Assert.That(
                    enterLiveCount,
                    Is.EqualTo(1),
                    "Clicking the SNAPSHOT badge switches to live mode."
                );

                using (
                    KeyDownEvent keyDown = KeyDownEvent.GetPooled(
                        '\n',
                        KeyCode.Return,
                        EventModifiers.None
                    )
                )
                {
                    keyDown.target = badge;
                    badge.SendEvent(keyDown);
                }

                Assert.That(
                    enterLiveCount,
                    Is.EqualTo(2),
                    "Return activates the badge the same way a click does."
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        /// <summary>
        /// Issue #344's second round: "The 'Type' should ideally be able to 'take to source'."
        /// The Monitor captures a type as an assembly-qualified name while the resolver reads
        /// the `Namespace.Type [Assembly]` shape, so the conversion between them is the piece
        /// that decides whether a link appears at all. A constructed generic carries its own
        /// comma-separated argument assemblies inside brackets, which is what breaks a naive
        /// split on the first comma.
        /// </summary>
        [TestCase(
            "Sample.Ping, Sample.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            "Sample.Ping [Sample.Runtime]"
        )]
        [TestCase(
            "Sample.Box`1[[Sample.Payload, Other.Asm, Version=1.0.0.0]], Sample.Runtime, Version=1.0.0.0",
            "Sample.Box`1[[Sample.Payload, Other.Asm, Version=1.0.0.0]] [Sample.Runtime]"
        )]
        [TestCase("Sample.Ping", "Sample.Ping")]
        [TestCase("", "")]
        [TestCase(null, "")]
        public void ACapturedTypeNameConvertsToTheShapeTheSourceResolverReads(
            string assemblyQualifiedName,
            string expected
        )
        {
            Assert.That(
                DxMessagingEditorSourceLinks.CreateSourceLookupName(assemblyQualifiedName),
                Is.EqualTo(expected)
            );
        }

        /// <summary>
        /// The positive half of "the Type takes you to source": a type whose declaring file the
        /// shared index really can find renders an Open-source button pointing at it. Without
        /// this, the only coverage was the negative case, which passes just as happily if
        /// resolution is broken outright.
        /// </summary>
        [Test]
        public void AResolvableTypeRendersAnOpenSourceLink()
        {
            DxMessagingEditorSourceLinks.ResetMessageSourceIndexesForTests();
            try
            {
                // Warm the lazy index the way the window does, then let it finish. `OlderMessage`
                // is declared in this file, so the location it resolves to is a real asset.
                _ = DxMessagingEditorSourceLinks.TryResolveSourceForAssemblyQualifiedName(
                    typeof(OlderMessage).AssemblyQualifiedName,
                    out _
                );
                DxMessagingEditorSourceLinks.CompleteMessageSourceIndexesForTests();

                Assert.That(
                    DxMessagingEditorSourceLinks.TryResolveSourceForAssemblyQualifiedName(
                        typeof(OlderMessage).AssemblyQualifiedName,
                        out DxMessagingEditorSourceLinks.SourceLocation location
                    ),
                    Is.True,
                    "A type declared in a compiled assembly's own source must resolve."
                );
                Assert.That(location.AssetPath, Does.EndWith(".cs"));

                MessageMonitorEntry entry = new(
                    nameof(OlderMessage),
                    "Context: Player",
                    CapturedStackTrace,
                    messageTypeIdentity: typeof(OlderMessage).AssemblyQualifiedName
                );
                VisualElement root = new();
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    new MessageMonitorSnapshot(
                        diagnosticsEnabled: true,
                        capacity: 8,
                        entries: new[] { entry }
                    )
                );

                VisualElement typeRow = root.Q<VisualElement>(
                    DxMessagingMessageMonitorWindow.DetailsTypeRowName
                );
                Assert.That(typeRow, Is.Not.Null);
                Button link = typeRow.Q<Button>(DxMessagingEditorSourceLinks.SourceLinkButtonName);
                Assert.That(
                    link,
                    Is.Not.Null,
                    "A type whose source resolves must offer a link to it."
                );
                Assert.That(link.tooltip, Does.Contain(location.AssetPath));
            }
            finally
            {
                DxMessagingEditorSourceLinks.ResetMessageSourceIndexesForTests();
            }
        }

        /// <summary>
        /// Source resolution is lazy: the first lookup answers "not yet" and finishes in the
        /// background. A window that does not listen for that completion renders a detail pane
        /// with no link and never revisits it, which is the state ask (4) shipped in until this
        /// subscription existed.
        /// </summary>
        [Test]
        public void TheWindowRerendersItsDetailPaneWhenTheSourceIndexCompletes()
        {
            DxMessagingMessageMonitorWindow window =
                ScriptableObject.CreateInstance<DxMessagingMessageMonitorWindow>();
            _createdWindows.Add(window);

            // Build a real surface on the window's own root, so the window's OnEnable
            // subscription is what routes the signal.
            VisualElement root = window.rootVisualElement;
            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 8,
                    entries: new[]
                    {
                        new MessageMonitorEntry(
                            nameof(OlderMessage),
                            "Context: Player",
                            CapturedStackTrace
                        ),
                    }
                )
            );

            ScrollView log = root.Q<ScrollView>(DxMessagingMessageMonitorWindow.ListName);
            VisualElement details = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.DetailsPaneName
            );
            Assert.That(log, Is.Not.Null);
            Assert.That(details, Is.Not.Null);

            DxMessagingEditorSourceLinks.RaiseMessageSourceIndexChangedForTests();

            Assert.That(
                root.Q<VisualElement>(DxMessagingMessageMonitorWindow.DetailsPaneName),
                Is.Not.SameAs(details),
                "A completed index must re-render the pane that can carry a source link; a window "
                    + "that does not listen shows a linkless pane forever."
            );
            Assert.That(
                root.Q<ScrollView>(DxMessagingMessageMonitorWindow.ListName),
                Is.SameAs(log),
                "Re-rendering for a link must not rebuild the log and take the reader's scroll "
                    + "position with it."
            );
        }

        /// <summary>
        /// A type whose declaring file cannot be found renders no link rather than a dead one.
        /// </summary>
        [Test]
        public void ATypeThatResolvesToNoSourceRendersNoLink()
        {
            MessageMonitorEntry entry = new(
                "NotARealType",
                "Context: Player",
                CapturedStackTrace,
                messageTypeIdentity: "Nowhere.NotARealType, Nowhere.Asm, Version=1.0.0.0"
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 8,
                    entries: new[] { entry }
                )
            );

            VisualElement typeRow = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.DetailsTypeRowName
            );
            Assert.That(typeRow, Is.Not.Null, "The detail pane always names the type.");
            Assert.That(
                typeRow.Q<Button>(DxMessagingEditorSourceLinks.SourceLinkButtonName),
                Is.Null,
                "A type with no resolvable source must not offer a link that opens nothing."
            );
        }

        [Test]
        public void BuildMonitorUiRendersModeBadgeAndListHeader()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            Label badge = root.Q<Label>(DxMessagingMessageMonitorWindow.ModeBadgeLabelName);
            Assert.That(badge, Is.Not.Null);
            Assert.That(
                badge.text,
                Is.EqualTo(DxMessagingMessageMonitorWindow.SnapshotModeBadgeText)
            );
            Assert.That(
                badge.tooltip,
                Is.EqualTo(DxMessagingMessageMonitorWindow.SnapshotModeHintText)
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.ModeHintLabelName).text,
                Is.EqualTo(DxMessagingMessageMonitorWindow.SnapshotModeHintText)
            );

            VisualElement header = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.ListHeaderName
            );
            Assert.That(header, Is.Not.Null);
            Assert.That(
                header.ClassListContains(DxMessagingEditorTheme.ListHeaderClassName),
                Is.True
            );
            CollectionAssert.AreEqual(
                new[] { "ROUTE", "MESSAGE", "CONTEXT", "#" },
                header.Query<Label>().ToList().ConvertAll(label => label.text)
            );
        }

        [Test]
        public void BuildMonitorUiKeepsSecondarySectionsCollapsed()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                MessageMonitorViewState.Default,
                componentEntries: Array.Empty<ComponentMonitorEntry>()
            );

            Foldout breakdown = root.Q<Foldout>(
                DxMessagingMessageMonitorWindow.BreakdownFoldoutName
            );
            Assert.That(breakdown, Is.Not.Null);
            Assert.That(breakdown.value, Is.False);
            Foldout components = root.Q<Foldout>(
                DxMessagingMessageMonitorWindow.ComponentFoldoutName
            );
            Assert.That(components, Is.Not.Null);
            Assert.That(components.value, Is.False);
        }

        /// <summary>
        /// Issue #344's "there is stuff rendered off screen" in its literal form, measured rather
        /// than argued: lay the real window out at the smallest size a user can drag it to and
        /// assert that nothing except the log's own scrolled content ends up past an edge.
        /// </summary>
        // 420x320 is the window's own minimum size. The smaller case is deliberately below it, as
        // headroom: the editor versions this package supports do not all give the same chrome the
        // same height, and 2021.3 overflowed at the minimum while 6000.x had room to spare. Each
        // size runs with the disclosures closed and open, because an expanded Breakdown is the
        // tallest thing the section ever holds.
        // 360x260 runs collapsed only: below the supported minimum, with every disclosure open at
        // once, there is genuinely less room than the sections' own floors add up to. That is a
        // limit of the window size, not a layout defect.
        [TestCase(360, 260, false)]
        [TestCase(420, 320, false)]
        [TestCase(420, 320, true)]
        [TestCase(420, 420, true)]
        [TestCase(520, 620, true)]
        [TestCase(900, 620, false)]
        [TestCase(900, 620, true)]
        public void BuildMonitorUiKeepsNonScrollingSectionsInsideTheWindow(
            int width,
            int height,
            bool expandDisclosures
        )
        {
            MessageMonitorEntry[] entries = Enumerable
                .Range(0, 24)
                .Select(index => new MessageMonitorEntry(
                    $"WallstopStudios.DxMessagingSamples.Diagnostics.LongEnoughMessageName{index:00}",
                    $"Context: Some Reasonably Long Scene Object Name {index:00} (9748{index:00})",
                    "UnityEngine.Debug:ExtractStackTraceNoAlloc (byte*,int,string)"
                ))
                .ToArray();
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 100,
                entries: entries
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                window.minSize = new Vector2(width, height);
                window.position = new Rect(0f, 0f, width, height);
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                root.style.width = width;
                root.style.height = height;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onRefresh: () => { },
                    onCopyExport: _ => { },
                    // A populated list, not an empty one: an expanded Component Diagnostics block
                    // with real rows is the tallest this section ever gets, and an empty list would
                    // never exercise the case it has to survive.
                    componentEntries: CreateComponentEntries(12)
                );

                if (expandDisclosures)
                {
                    root.Q<Foldout>(DxMessagingMessageMonitorWindow.BreakdownFoldoutName).value =
                        true;
                    root.Q<Foldout>(DxMessagingMessageMonitorWindow.ComponentFoldoutName).value =
                        true;
                }

                Assert.That(root.panel, Is.Not.Null, "A shown window must produce a panel.");
                EditorSurfaceCapture.InvokeInheritedPanelMethod(
                    root.panel,
                    "ValidateLayout",
                    Array.Empty<object>()
                );

                ScrollView log = root.Q<ScrollView>(DxMessagingMessageMonitorWindow.ListName);
                Assert.That(log, Is.Not.Null);
                Rect bounds = root.worldBound;
                List<string> escaped = new();
                foreach (VisualElement element in root.Query<VisualElement>().ToList())
                {
                    Rect elementBounds = element.worldBound;
                    if (elementBounds.width <= 0f || elementBounds.height <= 0f)
                    {
                        continue;
                    }
                    if (
                        elementBounds.yMax <= bounds.yMax + 0.5f
                        && elementBounds.xMax <= bounds.xMax + 0.5f
                    )
                    {
                        continue;
                    }
                    if (IsScrolledContent(element, root))
                    {
                        // Content below a scroll viewport is what that scroll view exists to reach.
                        continue;
                    }

                    escaped.Add(
                        $"{(string.IsNullOrEmpty(element.name) ? "<unnamed>" : element.name)} [{string.Join(" ", element.GetClasses())}] {elementBounds}"
                    );
                }

                Assert.That(
                    escaped,
                    Is.Empty,
                    $"At {width}x{height} these elements render outside the window: "
                        + string.Join("; ", escaped)
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        /// <summary>
        /// True when <paramref name="element"/> sits inside some scroll view's content, so being
        /// past the window edge means "scroll to reach it" rather than "rendered off screen".
        /// </summary>
        private static ComponentMonitorEntry[] CreateComponentEntries(int count)
        {
            return Enumerable
                .Range(0, count)
                .Select(index => new ComponentMonitorEntry(
                    $"Scene Root/Systems/Some Long Enough Object Name {index:00}",
                    nameof(MessagingComponent),
                    activeInHierarchy: index % 2 == 0,
                    listenerCount: 3,
                    enabledListenerCount: 2,
                    diagnosticsListenerCount: 1,
                    registrationCount: 7,
                    callCount: 42,
                    localEmissionCount: 5,
                    providerStatusText: "Provider: global bus",
                    warningText: index == 0 ? "Serialized provider missing" : string.Empty
                ))
                .ToArray();
        }

        private static bool IsScrolledContent(VisualElement element, VisualElement root)
        {
            for (
                VisualElement current = element.parent;
                current != null && current != root;
                current = current.parent
            )
            {
                if (current is ScrollView)
                {
                    return true;
                }
            }

            return false;
        }

        [Test]
        public void BuildMonitorUiRouteKindChipsCountAndFilterTheLog()
        {
            MessageMonitorEntry untargeted = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry targeted = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorEntry broadcast = CreateEntry(
                new BroadcastMessage(),
                new InstanceId(456)
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { untargeted, targeted, broadcast }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                MessageMonitorViewState observed = MessageMonitorViewState.Default;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    viewState => observed = viewState,
                    onCopyExport: _ => { }
                );

                Toggle targetedChip = root.Q<Toggle>(
                    DxMessagingMessageMonitorWindow.TargetedChipName
                );
                Assert.That(targetedChip, Is.Not.Null);
                Assert.That(targetedChip.text, Is.EqualTo("Targeted 1"));
                Assert.That(targetedChip.tooltip, Does.Contain("one target object"));
                Assert.That(
                    targetedChip.ClassListContains(DxMessagingEditorTheme.ChipTargetedClassName),
                    Is.True
                );
                Assert.That(
                    targetedChip.ClassListContains(DxMessagingEditorTheme.ChipWideClassName),
                    Is.True
                );
                Assert.That(
                    root.Q<Toggle>(DxMessagingMessageMonitorWindow.UntargetedChipName).text,
                    Is.EqualTo("Untargeted 1")
                );
                Assert.That(
                    root.Q<Toggle>(DxMessagingMessageMonitorWindow.BroadcastChipName).text,
                    Is.EqualTo("Broadcast 1")
                );

                targetedChip.value = false;

                Assert.That(observed.ShowTargeted, Is.False);
                List<string> visibleTypes = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList()
                    .ConvertAll(row =>
                        row.Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text
                    );
                CollectionAssert.AreEquivalent(
                    new[] { nameof(OlderMessage), nameof(BroadcastMessage) },
                    visibleTypes
                );
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Contain("2/3 shown")
                );
                Assert.That(
                    root.Q<Toggle>(DxMessagingMessageMonitorWindow.TargetedChipName).text,
                    Is.EqualTo("Targeted 1"),
                    "A hidden chip still counts what it would bring back."
                );

                targetedChip.value = true;

                Assert.That(observed.ShowTargeted, Is.True);
                Assert.That(
                    root.Query<VisualElement>(
                            className: DxMessagingMessageMonitorWindow.RowClassName
                        )
                        .ToList()
                        .Count,
                    Is.EqualTo(3)
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        /// <summary>
        /// Selecting a row must not rebuild the log. A rebuilt <see cref="ScrollView"/> starts at
        /// the top, so a reader who had scrolled into older rows would be thrown back to the newest
        /// one by the very click they used to look at an older one.
        /// </summary>
        [Test]
        public void BuildMonitorUiSelectingARowKeepsTheLogAndItsScrollPosition()
        {
            MessageMonitorEntry[] entries = Enumerable
                .Range(0, 12)
                .Select(index => new MessageMonitorEntry(
                    $"Message{index:00}",
                    $"Context: {index:00}",
                    string.Empty
                ))
                .ToArray();
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 32,
                entries: entries
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onCopyExport: _ => { }
                );

                ScrollView list = root.Q<ScrollView>(DxMessagingMessageMonitorWindow.ListName);
                Assert.That(list, Is.Not.Null);
                List<VisualElement> rows = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList();
                Assert.That(rows.Count, Is.EqualTo(12));

                SendClick(rows[5]);

                Assert.That(
                    root.Q<ScrollView>(DxMessagingMessageMonitorWindow.ListName),
                    Is.SameAs(list),
                    "Selecting a row must reuse the log, not rebuild it."
                );
                Assert.That(
                    root.Query<VisualElement>(
                            className: DxMessagingMessageMonitorWindow.RowClassName
                        )
                        .ToList()[5],
                    Is.SameAs(rows[5])
                );
                Assert.That(
                    rows[5].style.backgroundColor.value,
                    Is.EqualTo(DxMessagingEditorPalette.SelectedWash)
                );
                Assert.That(
                    rows[0].style.backgroundColor.keyword,
                    Is.EqualTo(StyleKeyword.Null),
                    "The previous selection's wash must be cleared, not repainted."
                );
                Assert.That(
                    root.Q<VisualElement>(DxMessagingMessageMonitorWindow.DetailsPaneName)
                        .Q<Label>(DxMessagingMessageMonitorWindow.DetailsTypeLabelName)
                        .text,
                    Is.EqualTo("Message05")
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        /// <summary>
        /// The Refresh button has to answer a synthesized <see cref="ClickEvent"/>, the event a real
        /// click produces. A <c>Button(Action)</c> installs a <c>Clickable</c> that only answers
        /// pointer down/up, which leaves the one control that re-reads the bus reachable only by a
        /// human.
        /// </summary>
        [Test]
        public void BuildMonitorUiRefreshButtonAnswersAClick()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            EditorWindow window = CreateTrackedEditorWindow();
            int refreshCount = 0;

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    window.rootVisualElement,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onRefresh: () => refreshCount++
                );

                Button refresh = window.rootVisualElement.Q<Button>(
                    DxMessagingMessageMonitorWindow.RefreshButtonName
                );
                Assert.That(refresh, Is.Not.Null);
                Assert.That(refresh.enabledSelf, Is.True);

                SendClick(refresh);

                Assert.That(refreshCount, Is.EqualTo(1));
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        /// <summary>
        /// A filter or chip change rebuilds the content, and a rebuilt <see cref="Foldout"/> starts
        /// closed. Opening Breakdown and then typing must not snap it shut, for the same reason a
        /// selection change does not rebuild the log: the reader put it there.
        /// </summary>
        [Test]
        public void BuildMonitorUiKeepsDisclosuresOpenAcrossAFilterChange()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onCopyExport: _ => { },
                    componentEntries: Array.Empty<ComponentMonitorEntry>()
                );

                root.Q<Foldout>(DxMessagingMessageMonitorWindow.BreakdownFoldoutName).value = true;
                root.Q<Foldout>(DxMessagingMessageMonitorWindow.ComponentFoldoutName).value = true;

                root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName).value = "Newer";

                Assert.That(
                    root.Q<Foldout>(DxMessagingMessageMonitorWindow.BreakdownFoldoutName).value,
                    Is.True,
                    "Typing in the filter must not close a disclosure the reader opened."
                );
                Assert.That(
                    root.Q<Foldout>(DxMessagingMessageMonitorWindow.ComponentFoldoutName).value,
                    Is.True
                );

                // Untargeted, not Targeted: the filter above already leaves only the targeted
                // entry, and hiding it would render the no-matches state, which has no Breakdown.
                root.Q<Toggle>(DxMessagingMessageMonitorWindow.UntargetedChipName).value = false;

                Assert.That(
                    root.Q<Foldout>(DxMessagingMessageMonitorWindow.BreakdownFoldoutName),
                    Is.Not.Null,
                    "Hiding a route kind with no visible rows must not empty the log."
                );
                Assert.That(
                    root.Q<Foldout>(DxMessagingMessageMonitorWindow.BreakdownFoldoutName).value,
                    Is.True,
                    "Toggling a taxonomy chip must not close a disclosure either."
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        /// <summary>
        /// Copy JSON has to answer a synthesized <see cref="ClickEvent"/> for the same reason
        /// Refresh does: <c>Button(Action)</c> installs a <c>Clickable</c> that only sees pointer
        /// down/up.
        /// </summary>
        [Test]
        public void BuildMonitorUiExportButtonAnswersAClick()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            EditorWindow window = CreateTrackedEditorWindow();
            string copied = null;

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    window.rootVisualElement,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onCopyExport: exportText => copied = exportText
                );

                Button export = window.rootVisualElement.Q<Button>(
                    DxMessagingMessageMonitorWindow.ExportButtonName
                );
                Assert.That(export, Is.Not.Null);
                Assert.That(export.enabledSelf, Is.True);

                SendClick(export);

                Assert.That(copied, Is.Not.Null);
                Assert.That(copied, Does.Contain(nameof(OlderMessage)));
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void CreateExportTextFollowsRouteKindChips()
        {
            MessageMonitorEntry untargeted = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry targeted = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { untargeted, targeted }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                DxMessagingMessageMonitorWindow.FilterEntries(
                    snapshot.Entries,
                    new MessageMonitorViewState(showTargeted: false)
                )
            );

            Assert.That(exportText, Does.Contain("\"entryCount\": 1"));
            Assert.That(exportText, Does.Contain(nameof(OlderMessage)));
            Assert.That(exportText, Does.Not.Contain(nameof(NewerMessage)));
        }

        [Test]
        public void BuildMonitorUiFiltersEntriesByTypeAndContext()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("123")
            );

            TextField filter = root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName);
            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.value, Is.EqualTo("123"));
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                Does.Contain("1/2 shown")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
        }

        [Test]
        public void BuildMonitorUiFiltersEntriesByMessageTypeFacet()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[]
                {
                    new MessageMonitorEntry(
                        "DuplicateMessage",
                        "Context: one",
                        string.Empty,
                        "Type.One.DuplicateMessage",
                        "Type.One.DuplicateMessage"
                    ),
                    new MessageMonitorEntry(
                        "DuplicateMessage",
                        "Context: two",
                        string.Empty,
                        "Type.Two.DuplicateMessage",
                        "Type.Two.DuplicateMessage"
                    ),
                }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("type:Type.Two")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                Does.Contain("two")
            );
        }

        [Test]
        public void BuildMonitorUiFiltersEntriesByContextAndStackFacets()
        {
            MessageMonitorEntry first = new(
                nameof(OlderMessage),
                "Context: Enemy",
                "Game.Combat.Apply"
            );
            MessageMonitorEntry second = new(
                nameof(NewerMessage),
                "Context: Player",
                "Game.Ui.Refresh"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { first, second }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("context:Enemy stack:Combat")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(OlderMessage))
            );
        }

        [Test]
        public void BuildMonitorUiFiltersEntriesByMessageAliasFacet()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("message:Newer")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
        }

        [Test]
        public void BuildMonitorUiPreservesPlainTextFilterAsWholeSubstring()
        {
            MessageMonitorEntry entry = new(
                nameof(NewerMessage),
                "Context: Player",
                "Game.Ui.Refresh"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("NewerMessage Player")
            );

            Assert.That(
                root.Query<VisualElement>(className: DxMessagingMessageMonitorWindow.RowClassName)
                    .ToList(),
                Is.Empty
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName).text,
                Does.Contain("No messages match")
            );
        }

        [Test]
        public void BuildMonitorUiPreservesFieldLookingPlainTextAsWholeSubstring()
        {
            MessageMonitorEntry playerType = new("PlayerAlert", "Context: Enemy", string.Empty);
            MessageMonitorEntry playerContext = new(
                nameof(OlderMessage),
                "Context: Player",
                string.Empty
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { playerType, playerContext }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("Context: Player")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                Is.EqualTo("Context: Player")
            );
        }

        [Test]
        public void BuildMonitorUiDoesNotPartiallyScopeSpacedFacetValues()
        {
            MessageMonitorEntry entry = new("ShipMessage", "Context: Player", string.Empty);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("context:Player Ship")
            );

            Assert.That(
                root.Query<VisualElement>(className: DxMessagingMessageMonitorWindow.RowClassName)
                    .ToList(),
                Is.Empty
            );
        }

        [Test]
        public void BuildMonitorUiRendersActiveTypedFilterSummary()
        {
            MessageMonitorEntry entry = new(
                nameof(NewerMessage),
                "Context: Player",
                "Game.Ui.Refresh"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("type:Newer context:Player")
            );

            VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
            Assert.That(summary, Is.Not.Null);
            Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            AssertCompleteBorder(summary, DxMessagingEditorPalette.Amber);
            Assert.That(
                summary.Q<Label>(ActiveFilterSummaryLabelName).text,
                Is.EqualTo("Active typed filters")
            );
            CollectionAssert.AreEqual(
                new[] { "type:Newer", "context:Player" },
                summary
                    .Query<Label>(className: ActiveFilterTokenClassName)
                    .ToList()
                    .ConvertAll(label => label.text)
            );
            Assert.That(summary.Q<Button>(ActiveFilterClearButtonName), Is.Not.Null);
        }

        [Test]
        public void BuildMonitorUiRendersActivePlainTextFilterSummary()
        {
            MessageMonitorEntry entry = new(
                nameof(NewerMessage),
                "Context: Player",
                "Game.Ui.Refresh"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("Context: Player")
            );

            VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
            Assert.That(summary, Is.Not.Null);
            Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                summary.Q<Label>(ActiveFilterSummaryLabelName).text,
                Is.EqualTo("Active text filter")
            );
            CollectionAssert.AreEqual(
                new[] { "Context: Player" },
                summary
                    .Query<Label>(className: ActiveFilterTokenClassName)
                    .ToList()
                    .ConvertAll(label => label.text)
            );
        }

        [Test]
        public void BuildMonitorUiHidesActiveFilterSummaryWithoutFilter()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
            Assert.That(summary, Is.Not.Null);
            Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(
                summary.Query<Label>(className: ActiveFilterTokenClassName).ToList(),
                Is.Empty
            );
        }

        [Test]
        public void BuildMonitorUiClearButtonClearsFilterTextAndCallback()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            EditorWindow window = CreateTrackedEditorWindow();
            string observedFilter = null;

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    new MessageMonitorViewState("type:Newer"),
                    viewState => observedFilter = viewState.FilterText,
                    onCopyExport: _ => { }
                );

                TextField filter = root.Q<TextField>(
                    DxMessagingMessageMonitorWindow.FilterFieldName
                );
                VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
                Button clear = summary.Q<Button>(ActiveFilterClearButtonName);

                Assert.That(filter.value, Is.EqualTo("type:Newer"));
                Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.Flex));

                SendClick(clear);

                Assert.That(observedFilter, Is.EqualTo(string.Empty));
                Assert.That(filter.value, Is.EqualTo(string.Empty));
                Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.None));
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiClearButtonUpdatesAttachedUiWithoutFilterCallback()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    new MessageMonitorViewState("type:Missing"),
                    onCopyExport: _ => { }
                );

                TextField filter = root.Q<TextField>(
                    DxMessagingMessageMonitorWindow.FilterFieldName
                );
                VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
                Button clear = summary.Q<Button>(ActiveFilterClearButtonName);
                Button export = root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName);

                Assert.That(filter.value, Is.EqualTo("type:Missing"));
                Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(export.enabledSelf, Is.False);
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Contain("0/2 shown")
                );
                Assert.That(
                    root.Query<VisualElement>(
                            className: DxMessagingMessageMonitorWindow.RowClassName
                        )
                        .ToList(),
                    Is.Empty
                );

                SendClick(clear);

                Assert.That(filter.value, Is.EqualTo(string.Empty));
                Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.None));
                Assert.That(export.enabledSelf, Is.True);
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Not.Contain("shown")
                );
                Assert.That(
                    root.Query<VisualElement>(
                            className: DxMessagingMessageMonitorWindow.RowClassName
                        )
                        .ToList()
                        .Count,
                    Is.EqualTo(2)
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiKeepsClearReachableForLongActiveFilter()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            string longFilter = string.Join(
                " ",
                Enumerable.Range(0, 24).Select(index => $"type:Message{index:00}")
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState(longFilter)
            );

            VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
            Button clear = summary.Q<Button>(ActiveFilterClearButtonName);
            ScrollView tokenScroll = summary.Q<ScrollView>(ActiveFilterTokenScrollViewName);
            List<VisualElement> children = summary.Children().ToList();

            Assert.That(clear, Is.Not.Null);
            Assert.That(tokenScroll, Is.Not.Null);
            Assert.That(children.IndexOf(clear), Is.LessThan(children.IndexOf(tokenScroll)));
            Assert.That(tokenScroll.style.maxHeight.value.value, Is.EqualTo(72f));
            Assert.That(
                tokenScroll.Query<Label>(className: ActiveFilterTokenClassName).ToList().Count,
                Is.EqualTo(24)
            );
        }

        [Test]
        public void CreateExportTextDoesNotExportActiveFilterSummary()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                DxMessagingMessageMonitorWindow.FilterEntries(
                    snapshot.Entries,
                    new MessageMonitorViewState("type:Newer")
                )
            );

            Assert.That(exportText, Does.Not.Contain("activeFilter"));
            Assert.That(exportText, Does.Not.Contain("filterSummary"));
            Assert.That(exportText, Does.Contain(nameof(NewerMessage)));
            Assert.That(exportText, Does.Not.Contain(nameof(OlderMessage)));
        }

        [Test]
        public void BuildMonitorUiRendersVisibleContextLanesFromVisibleEntries()
        {
            MessageMonitorEntry enemyOlder = new(
                nameof(OlderMessage),
                "Context: Enemy",
                string.Empty
            );
            MessageMonitorEntry playerOlder = new(
                nameof(OlderMessage),
                "Context: Player",
                string.Empty
            );
            MessageMonitorEntry playerNewer = new(
                nameof(NewerMessage),
                "Context: Player",
                string.Empty
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { playerNewer, playerOlder, enemyOlder }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement lanes = root.Q<VisualElement>(ContextLanesName);
            Assert.That(lanes, Is.Not.Null);
            Assert.That(
                lanes.Q<Label>(ContextLanesSummaryLabelName).text,
                Is.EqualTo(
                    "2 context lanes | Entries: 3 | Busiest context: Context: Player | Share: 2/3 (67%)"
                )
            );

            List<VisualElement> rows = lanes
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(ContextLaneContextLabelName).text,
                Is.EqualTo("Context: Player")
            );
            Assert.That(
                rows[0].Q<Label>(ContextLaneSummaryLabelName).text,
                Is.EqualTo("2 - 67%"),
                "A lane pill shows its count and share; the counts behind them stay in the tooltip."
            );
            string tooltip = rows[0].Q<Button>(ContextLaneFilterButtonName).tooltip;
            Assert.That(tooltip, Does.Contain("Entries: 2 | Message types: 2 | Share: 2/3 (67%)"));
            Assert.That(tooltip, Does.Contain(nameof(OlderMessage)));
            Assert.That(tooltip, Does.Contain(nameof(NewerMessage)));
            Assert.That(
                rows[1].Q<Label>(ContextLaneContextLabelName).text,
                Is.EqualTo("Context: Enemy")
            );
        }

        [Test]
        public void BuildMonitorUiUsesCompleteBordersForVisibleLaneGroups()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[]
                {
                    new MessageMonitorEntry(nameof(OlderMessage), "Context: Player", string.Empty),
                    new MessageMonitorEntry(nameof(NewerMessage), "Context: HUD", string.Empty),
                }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            AssertCompleteBorder(
                root.Q<VisualElement>(MessageTypeLanesName),
                DxMessagingEditorPalette.BorderPanel
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(ContextLanesName),
                DxMessagingEditorPalette.BorderPanel
            );
        }

        [Test]
        public void BuildMonitorUiScopesVisibleContextLanesToFilteredEntries()
        {
            MessageMonitorEntry older = new(nameof(OlderMessage), "Context: Enemy", string.Empty);
            MessageMonitorEntry newer = new(nameof(NewerMessage), "Context: Player", string.Empty);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("context:Player")
            );

            VisualElement lanes = root.Q<VisualElement>(ContextLanesName);
            Assert.That(lanes, Is.Not.Null);
            Assert.That(
                lanes.Q<Label>(ContextLanesSummaryLabelName).text,
                Is.EqualTo(
                    "1 context lane | Entries: 1 | Busiest context: Context: Player | Share: 1/1 (100%)"
                )
            );

            List<VisualElement> rows = lanes
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(ContextLaneContextLabelName).text,
                Is.EqualTo("Context: Player")
            );
        }

        [Test]
        public void BuildMonitorUiContextLaneFilterButtonAppliesVisibleContextFilter()
        {
            MessageMonitorEntry enemy = new(nameof(OlderMessage), "Context: Enemy", string.Empty);
            MessageMonitorEntry player = new(nameof(NewerMessage), "Context: Player", string.Empty);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { player, enemy }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onCopyExport: _ => { }
                );

                VisualElement playerLane = root.Q<VisualElement>(ContextLanesName)
                    .Query<VisualElement>(className: ContextLaneRowClassName)
                    .ToList()
                    .First(row =>
                        row.Q<Label>(ContextLaneContextLabelName).text == "Context: Player"
                    );
                Button filterButton = playerLane.Q<Button>(ContextLaneFilterButtonName);
                Assert.That(filterButton, Is.Not.Null);

                SendClick(filterButton);

                Assert.That(
                    root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName).value,
                    Is.EqualTo("context:\"Context: Player\"")
                );
                Assert.That(
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Q<Label>(ActiveFilterSummaryLabelName)
                        .text,
                    Is.EqualTo("Active typed filters")
                );
                CollectionAssert.AreEqual(
                    new[] { "context:\"Context: Player\"" },
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Query<Label>(className: ActiveFilterTokenClassName)
                        .ToList()
                        .ConvertAll(label => label.text)
                );
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Contain("1/2 shown")
                );

                List<VisualElement> rows = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList();
                Assert.That(rows.Count, Is.EqualTo(1));
                Assert.That(
                    rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                    Is.EqualTo("Context: Player")
                );
                Assert.That(
                    root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName).enabledSelf,
                    Is.True
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiContextLaneFilterButtonMatchesOverlappingContextExactly()
        {
            MessageMonitorEntry player = new(nameof(NewerMessage), "Context: Player", string.Empty);
            MessageMonitorEntry ship = new(
                nameof(OlderMessage),
                "Context: Player Ship",
                string.Empty
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { ship, player }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onCopyExport: _ => { }
                );

                VisualElement playerLane = root.Q<VisualElement>(ContextLanesName)
                    .Query<VisualElement>(className: ContextLaneRowClassName)
                    .ToList()
                    .First(row =>
                        row.Q<Label>(ContextLaneContextLabelName).text == "Context: Player"
                    );
                Button filterButton = playerLane.Q<Button>(ContextLaneFilterButtonName);
                Assert.That(filterButton, Is.Not.Null);

                SendClick(filterButton);

                Assert.That(
                    root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName).value,
                    Is.EqualTo("context:\"Context: Player\"")
                );
                Assert.That(
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Q<Label>(ActiveFilterSummaryLabelName)
                        .text,
                    Is.EqualTo("Active typed filters")
                );
                CollectionAssert.AreEqual(
                    new[] { "context:\"Context: Player\"" },
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Query<Label>(className: ActiveFilterTokenClassName)
                        .ToList()
                        .ConvertAll(label => label.text)
                );
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Contain("1/2 shown")
                );

                List<VisualElement> rows = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList();
                Assert.That(rows.Count, Is.EqualTo(1));
                Assert.That(
                    rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                    Is.EqualTo("Context: Player")
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiKeepsDistinctContextLaneMessagesForSameSimpleNames()
        {
            MessageMonitorEntry first = CreateEntry(new CollisionOne.DuplicateMessage(), null);
            MessageMonitorEntry second = CreateEntry(new CollisionTwo.DuplicateMessage(), null);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { second, first }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(ContextLanesName)
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].Q<Label>(ContextLaneSummaryLabelName).text, Is.EqualTo("2 - 100%"));
            string tooltip = rows[0].Q<Button>(ContextLaneFilterButtonName).tooltip;
            Assert.That(tooltip, Does.Contain("Entries: 2 | Message types: 2 | Share: 2/2 (100%)"));
            Assert.That(tooltip, Does.Contain("CollisionOne.DuplicateMessage"));
            Assert.That(tooltip, Does.Contain("CollisionTwo.DuplicateMessage"));
        }

        [Test]
        public void BuildMonitorUiKeepsDistinctContextLaneMessagesAcrossSplitContexts()
        {
            MessageMonitorEntry first = new(
                "DuplicateMessage",
                "Context: Player",
                string.Empty,
                "Collision.One.DuplicateMessage",
                "CollisionOne.DuplicateMessage"
            );
            MessageMonitorEntry second = new(
                "DuplicateMessage",
                "Context: Enemy",
                string.Empty,
                "Collision.Two.DuplicateMessage",
                "CollisionTwo.DuplicateMessage"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { second, first }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(ContextLanesName)
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Button>(ContextLaneFilterButtonName).tooltip,
                Does.Contain("CollisionTwo.DuplicateMessage")
            );
            Assert.That(
                rows[1].Q<Button>(ContextLaneFilterButtonName).tooltip,
                Does.Contain("CollisionOne.DuplicateMessage")
            );
        }

        [Test]
        public void BuildMonitorUiBoundsVisibleContextLaneRows()
        {
            MessageMonitorEntry[] entries = Enumerable
                .Range(0, 24)
                .Select(index => new MessageMonitorEntry(
                    $"Message{index:00}",
                    $"Context: {index:00}",
                    string.Empty
                ))
                .ToArray();
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 32,
                entries: entries
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement lanes = root.Q<VisualElement>(ContextLanesName);
            ScrollView scroll = lanes.Q<ScrollView>(ContextLaneScrollViewName);
            Assert.That(scroll, Is.Not.Null);
            Assert.That(scroll.style.maxHeight.value.value, Is.EqualTo(96f));
            Assert.That(
                scroll.Query<VisualElement>(className: ContextLaneRowClassName).ToList().Count,
                Is.EqualTo(24)
            );
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleContextLaneAggregates()
        {
            MessageMonitorEntry older = new(nameof(OlderMessage), "Context: Enemy", string.Empty);
            MessageMonitorEntry newer = new(nameof(NewerMessage), "Context: Player", string.Empty);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                snapshot.Entries
            );

            Assert.That(exportText, Does.Not.Contain("contextLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleContextLanes"));
        }

        [Test]
        public void BuildMonitorUiWiresAttachedFilterAndRowCallbacksWithoutRebuildingRoot()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            EditorWindow window = CreateTrackedEditorWindow();
            string observedFilter = null;
            int observedSelectedEntryIndex = -1;

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    viewState =>
                    {
                        observedFilter = viewState.FilterText;
                        observedSelectedEntryIndex = viewState.SelectedEntryIndex;
                    },
                    onCopyExport: _ => { }
                );

                TextField filter = root.Q<TextField>(
                    DxMessagingMessageMonitorWindow.FilterFieldName
                );
                Button refresh = root.Q<Button>(DxMessagingMessageMonitorWindow.RefreshButtonName);
                Button export = root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName);
                List<VisualElement> rows = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList();
                int childCountBeforeFilterChange = root.childCount;

                Assert.That(refresh.enabledSelf, Is.False);
                Assert.That(export.enabledSelf, Is.True);
                Assert.That(rows.Count, Is.EqualTo(2));

                using (ClickEvent click = ClickEvent.GetPooled())
                {
                    click.target = rows[1];
                    rows[1].SendEvent(click);
                }
                Assert.That(observedSelectedEntryIndex, Is.EqualTo(1));

                filter.value = "missing";

                Assert.That(observedFilter, Is.EqualTo("missing"));
                Assert.That(root.childCount, Is.EqualTo(childCountBeforeFilterChange));
                Assert.That(export.enabledSelf, Is.False);

                filter.value = "123";

                Assert.That(observedFilter, Is.EqualTo("123"));
                Assert.That(root.childCount, Is.EqualTo(childCountBeforeFilterChange));
                Assert.That(export.enabledSelf, Is.True);
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiRendersNoFilteredMatchesState()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("missing")
            );

            Assert.That(
                root.Query<VisualElement>(className: DxMessagingMessageMonitorWindow.RowClassName)
                    .ToList(),
                Is.Empty
            );
            Label emptyTitle = root.Q<Label>(
                DxMessagingMessageMonitorWindow.EmptyStateTitleLabelName
            );
            Assert.That(emptyTitle, Is.Not.Null);
            Assert.That(emptyTitle.text, Is.EqualTo("No matches"));
            Label emptyBody = root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName);
            Assert.That(emptyBody, Is.Not.Null);
            Assert.That(emptyBody.text, Does.Contain("No messages match"));
            Assert.That(
                root.Q<VisualElement>(DxMessagingMessageMonitorWindow.DetailsPaneName),
                Is.Null
            );
        }

        [Test]
        public void BuildMonitorUiRendersSelectedEntryDetails()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState(selectedEntryIndex: 1)
            );

            VisualElement details = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.DetailsPaneName
            );
            Assert.That(details, Is.Not.Null);
            Assert.That(
                details.Q<Label>(DxMessagingMessageMonitorWindow.DetailsTypeLabelName).text,
                Does.Contain(nameof(OlderMessage))
            );
            Assert.That(
                details.Q<Label>(DxMessagingMessageMonitorWindow.DetailsContextLabelName).text,
                Does.Contain("none")
            );
            Label badge = details
                .Query<Label>(className: DxMessagingEditorTheme.TypeBadgeClassName)
                .First();
            Assert.That(badge.text, Is.EqualTo(DxMessagingEditorPalette.UntargetedKind));
            Assert.That(
                badge.ClassListContains(
                    ExpectedTypeBadgeClass(DxMessagingEditorPalette.UntargetedKind)
                ),
                Is.True
            );
        }

        [Test]
        public void BuildMonitorUiRendersVisibleMessageTypeLanesFromVisibleEntries()
        {
            MessageMonitorEntry olderWithoutContext = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry olderWithContext = CreateEntry(
                new OlderMessage(),
                new InstanceId(42)
            );
            MessageMonitorEntry newerWithContext = CreateEntry(
                new NewerMessage(),
                new InstanceId(123)
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newerWithContext, olderWithContext, olderWithoutContext }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement lanes = root.Q<VisualElement>(MessageTypeLanesName);
            Assert.That(lanes, Is.Not.Null);
            Assert.That(
                lanes.Q<Label>(MessageTypeLanesSummaryLabelName).text,
                Is.EqualTo(
                    "2 message type lanes | Entries: 3 | Busiest message type: OlderMessage | Share: 2/3 (67%)"
                )
            );

            List<VisualElement> rows = lanes
                .Query<VisualElement>(className: MessageTypeLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Is.EqualTo(nameof(OlderMessage))
            );
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneSummaryLabelName).text,
                Is.EqualTo("2 - 67%")
            );
            string tooltip = rows[0].Q<Button>(MessageTypeLaneFilterButtonName).tooltip;
            Assert.That(tooltip, Does.Contain("Entries: 2 | Contexts: 2 | Share: 2/3 (67%)"));
            Assert.That(tooltip, Does.Contain("Context: 42"));
            Assert.That(tooltip, Does.Contain("Context: none"));
            Assert.That(
                rows[1].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
        }

        [Test]
        public void BuildMonitorUiBoundsVisibleMessageTypeLaneRows()
        {
            MessageMonitorEntry[] entries = Enumerable
                .Range(0, 24)
                .Select(index => new MessageMonitorEntry(
                    $"Message{index:00}",
                    $"Context: {index:00}",
                    string.Empty
                ))
                .ToArray();
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 32,
                entries: entries
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement lanes = root.Q<VisualElement>(MessageTypeLanesName);
            ScrollView scroll = lanes.Q<ScrollView>(MessageTypeLaneScrollViewName);
            Assert.That(scroll, Is.Not.Null);
            Assert.That(scroll.style.maxHeight.value.value, Is.EqualTo(96f));
            Assert.That(
                scroll.Query<VisualElement>(className: MessageTypeLaneRowClassName).ToList().Count,
                Is.EqualTo(24)
            );
        }

        [Test]
        public void BuildMonitorUiKeepsDistinctMessageTypeIdentityForSameSimpleNames()
        {
            MessageMonitorEntry first = CreateEntry(
                new CollisionOne.DuplicateMessage(),
                new InstanceId(1)
            );
            MessageMonitorEntry second = CreateEntry(
                new CollisionTwo.DuplicateMessage(),
                new InstanceId(2)
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { second, first }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(MessageTypeLanesName)
                .Query<VisualElement>(className: MessageTypeLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Does.Contain("CollisionOne.DuplicateMessage")
            );
            Assert.That(
                rows[1].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Does.Contain("CollisionTwo.DuplicateMessage")
            );
        }

        [Test]
        public void BuildMonitorUiScopesVisibleMessageTypeLanesToFilteredEntries()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState(nameof(NewerMessage))
            );

            VisualElement lanes = root.Q<VisualElement>(MessageTypeLanesName);
            Assert.That(lanes, Is.Not.Null);
            Assert.That(
                lanes.Q<Label>(MessageTypeLanesSummaryLabelName).text,
                Is.EqualTo(
                    "1 message type lane | Entries: 1 | Busiest message type: NewerMessage | Share: 1/1 (100%)"
                )
            );

            List<VisualElement> rows = lanes
                .Query<VisualElement>(className: MessageTypeLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
        }

        [Test]
        public void BuildMonitorUiMessageTypeLaneFilterButtonAppliesTypedFilter()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onCopyExport: _ => { }
                );

                VisualElement newerLane = root.Q<VisualElement>(MessageTypeLanesName)
                    .Query<VisualElement>(className: MessageTypeLaneRowClassName)
                    .ToList()
                    .First(row =>
                        row.Q<Label>(MessageTypeLaneTypeLabelName).text == nameof(NewerMessage)
                    );
                Button filterButton = newerLane.Q<Button>(MessageTypeLaneFilterButtonName);
                Assert.That(filterButton, Is.Not.Null);

                SendClick(filterButton);

                Assert.That(
                    root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName).value,
                    Is.EqualTo("type:NewerMessage")
                );
                Assert.That(
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Q<Label>(ActiveFilterSummaryLabelName)
                        .text,
                    Is.EqualTo("Active typed filters")
                );
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Contain("1/2 shown")
                );

                List<VisualElement> rows = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList();
                Assert.That(rows.Count, Is.EqualTo(1));
                Assert.That(
                    rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                    Is.EqualTo(nameof(NewerMessage))
                );
                Assert.That(
                    root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName).enabledSelf,
                    Is.True
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleMessageTypeLaneAggregates()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                snapshot.Entries
            );

            Assert.That(exportText, Does.Not.Contain("messageTypeLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleMessageTypeLanes"));
        }

        [Test]
        public void BuildMonitorUiRendersComponentDiagnosticsPanel()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: Array.Empty<MessageMonitorEntry>()
            );
            ComponentMonitorEntry component = new(
                "Root/Emitter",
                "MessagingComponent",
                activeInHierarchy: true,
                listenerCount: 2,
                enabledListenerCount: 1,
                diagnosticsListenerCount: 1,
                registrationCount: 3,
                callCount: 7,
                localEmissionCount: 4,
                providerStatusText: "Provider: global bus",
                warningText: "Serialized provider missing"
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot, new[] { component });

            VisualElement panel = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.ComponentPanelName
            );
            Assert.That(panel, Is.Not.Null);
            AssertCompleteBorder(panel, DxMessagingEditorPalette.BorderPanel);

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.ComponentRowClassName
                )
                .ToList();
            Assert.That(
                root.Q<ScrollView>(DxMessagingMessageMonitorWindow.ComponentScrollViewName),
                Is.Not.Null
            );
            Assert.That(rows.Count, Is.EqualTo(1));
            AssertCompleteBorder(rows[0], DxMessagingEditorPalette.Amber);
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentNameLabelName).text,
                Does.Contain("Root/Emitter")
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentSummaryLabelName).text,
                Does.Contain("Registrations: 3")
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentSummaryLabelName).text,
                Does.Contain("Calls: 7")
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentProviderLabelName).text,
                Does.Contain("global bus")
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentWarningLabelName).text,
                Does.Contain("Serialized provider missing")
            );
        }

        [Test]
        public void BuildMonitorUiKeepsComponentPanelVisibleWhenSnapshotUnavailable()
        {
            MessageMonitorSnapshot snapshot = MessageMonitorSnapshot.Unavailable(
                "The active global bus is not the default DxMessaging MessageBus."
            );
            ComponentMonitorEntry component = new(
                "Root/Listener",
                "MessagingComponent",
                activeInHierarchy: true,
                listenerCount: 1,
                enabledListenerCount: 1,
                diagnosticsListenerCount: 0,
                registrationCount: 1,
                callCount: 0,
                localEmissionCount: 0,
                providerStatusText: "Provider: runtime provider",
                warningText: string.Empty
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot, new[] { component });

            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                Is.EqualTo("Unavailable")
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName).text,
                Does.Contain("active global bus")
            );
            Assert.That(
                root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName),
                Is.Null
            );
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.ComponentRowClassName
                    )
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
        }

        [Test]
        public void BuildMonitorUiKeepsComponentPanelVisibleWhenMessageDiagnosticsAreOff()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: false,
                capacity: 8,
                entries: Array.Empty<MessageMonitorEntry>()
            );
            ComponentMonitorEntry component = new(
                "Root/Listener",
                "MessagingComponent",
                activeInHierarchy: false,
                listenerCount: 1,
                enabledListenerCount: 0,
                diagnosticsListenerCount: 0,
                registrationCount: 1,
                callCount: 0,
                localEmissionCount: 0,
                providerStatusText: "Provider: global bus",
                warningText: string.Empty
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot, new[] { component });

            Label emptyTitle = root.Q<Label>(
                DxMessagingMessageMonitorWindow.EmptyStateTitleLabelName
            );
            Assert.That(emptyTitle, Is.Not.Null);
            Assert.That(emptyTitle.text, Is.EqualTo("Diagnostics are Off"));
            Label emptyBody = root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName);
            Assert.That(emptyBody, Is.Not.Null);
            Assert.That(emptyBody.text, Does.Contain("Enable diagnostics"));
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.ComponentRowClassName
                    )
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.ComponentNameLabelName).text,
                Does.Contain("inactive")
            );
        }

        [Test]
        public void CaptureComponentSnapshotsReadsMessagingComponentHarnessState()
        {
            GameObject host = CreateTrackedObject("ComponentPanelHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(messageBus, Is.Not.Null);
            int initialRegistrationCount = CountMessageBusRegistrations(messageBus);

            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.DiagnosticMode = true;
            token.RegisterUntargeted<OlderMessage>(listener.OnOlderMessage);
            token.Enable();

            messageBus.DiagnosticsMode = true;
            messageBus._emissionBuffer.Clear();

            OlderMessage message = default;
            MessageHandler.MessageBus.UntargetedBroadcast(ref message);

            IReadOnlyList<ComponentMonitorEntry> components =
                DxMessagingMessageMonitorWindow.CaptureComponentSnapshots(
                    new[] { messagingComponent }
                );

            Assert.That(components.Count, Is.EqualTo(1));
            Assert.That(components[0].HierarchyPath, Is.EqualTo("ComponentPanelHost"));
            Assert.That(components[0].ListenerCount, Is.EqualTo(1));
            Assert.That(components[0].EnabledListenerCount, Is.EqualTo(1));
            Assert.That(components[0].DiagnosticsListenerCount, Is.EqualTo(1));
            Assert.That(components[0].RegistrationCount, Is.EqualTo(1));
            Assert.That(components[0].CallCount, Is.EqualTo(1));
            Assert.That(components[0].LocalEmissionCount, Is.GreaterThan(0));
            Assert.That(components[0].ProviderStatusText, Does.Contain("global bus"));
            Assert.That(components[0].WarningText, Is.Empty);

            messagingComponent.EditorResetRuntimeState();
            Assert.That(
                CountMessageBusRegistrations(messageBus),
                Is.EqualTo(initialRegistrationCount)
            );
        }

        [Test]
        public void CaptureComponentSnapshotsFindsSceneComponentsAndSkipsPersistentAssets()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string sceneName = "SceneComponentHost-" + suffix;
            string prefabName = "PrefabComponentHost-" + suffix;
            string prefabPath = $"Assets/{prefabName}.prefab";
            GameObject sceneHost = CreateTrackedObject(sceneName);
            MessagingComponent sceneComponent = sceneHost.AddComponent<MessagingComponent>();
            GameObject prefabHost = new(prefabName);
            _createdAssetPaths.Add(prefabPath);

            try
            {
                prefabHost.AddComponent<MessagingComponent>();
                GameObject prefabAsset = null;
                EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                    prefabAsset = PrefabUtility.SaveAsPrefabAsset(prefabHost, prefabPath)
                );
                Assert.That(prefabAsset, Is.Not.Null);
                Object.DestroyImmediate(prefabHost);
                prefabHost = null;

                MessagingComponent prefabComponent = prefabAsset.GetComponent<MessagingComponent>();
                Assert.That(prefabComponent, Is.Not.Null);
                Assert.That(EditorUtility.IsPersistent(prefabComponent), Is.True);
                MessagingComponent[] unfiltered = Array.Empty<MessagingComponent>();
                EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                    unfiltered = Resources.FindObjectsOfTypeAll<MessagingComponent>()
                );
                Assert.That(unfiltered, Has.Member(sceneComponent));
                Assert.That(unfiltered, Has.Member(prefabComponent));

                IReadOnlyList<ComponentMonitorEntry> components =
                    Array.Empty<ComponentMonitorEntry>();
                EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                    components = DxMessagingMessageMonitorWindow.CaptureComponentSnapshots()
                );

                Assert.That(
                    components.Any(component => component.HierarchyPath == sceneName),
                    Is.True
                );
                Assert.That(
                    components.Any(component => component.HierarchyPath == prefabName),
                    Is.False
                );
            }
            finally
            {
                if (prefabHost != null)
                {
                    Object.DestroyImmediate(prefabHost);
                }
            }
        }

        [Test]
        public void CaptureComponentSnapshotsDoesNotResolveSerializedProviders()
        {
            ThrowingScriptableMessageBusProvider provider = CreateTrackedObject(
                ScriptableObject.CreateInstance<ThrowingScriptableMessageBusProvider>()
            );
            GameObject host = CreateTrackedObject("SerializedProviderHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            SerializedObject serializedObject = new(messagingComponent);
            SerializedProperty handleProperty = serializedObject.FindProperty(
                "_serializedProviderHandle"
            );
            Assert.That(handleProperty, Is.Not.Null);
            SerializedProperty providerProperty = handleProperty.FindPropertyRelative("_provider");
            Assert.That(providerProperty, Is.Not.Null);
            providerProperty.objectReferenceValue = provider;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            IReadOnlyList<ComponentMonitorEntry> components =
                DxMessagingMessageMonitorWindow.CaptureComponentSnapshots(
                    new[] { messagingComponent }
                );

            Assert.That(components.Count, Is.EqualTo(1));
            Assert.That(provider.ResolveCount, Is.EqualTo(0));
            Assert.That(components[0].ProviderStatusText, Does.Contain("serialized provider"));
            Assert.That(components[0].WarningText, Is.Empty);
        }

        [Test]
        public void CaptureComponentSnapshotsSkipsPreviewSceneComponents()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string sceneName = "MonitorSceneHost-" + suffix;
            string previewName = "MonitorPreviewHost-" + suffix;
            GameObject sceneHost = CreateTrackedObject(sceneName);
            sceneHost.AddComponent<MessagingComponent>();
            Scene previewScene = EditorSceneManager.NewPreviewScene();
            GameObject previewHost = new(previewName);

            try
            {
                SceneManager.MoveGameObjectToScene(previewHost, previewScene);
                MessagingComponent previewComponent =
                    previewHost.AddComponent<MessagingComponent>();
                Assert.That(previewComponent.gameObject.scene.IsValid(), Is.True);
                Assert.That(EditorSceneManager.IsPreviewSceneObject(previewHost), Is.True);

                IReadOnlyList<ComponentMonitorEntry> components =
                    Array.Empty<ComponentMonitorEntry>();
                EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                    components = DxMessagingMessageMonitorWindow.CaptureComponentSnapshots()
                );

                Assert.That(
                    components.Any(component => component.HierarchyPath == sceneName),
                    Is.True
                );
                Assert.That(
                    components.Any(component => component.HierarchyPath == previewName),
                    Is.False
                );
            }
            finally
            {
                if (previewHost != null)
                {
                    Object.DestroyImmediate(previewHost);
                }
                if (previewScene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(previewScene);
                }
            }
        }

        [Test]
        public void CaptureComponentSnapshotsReportsPerComponentCaptureFailures()
        {
            GameObject host = CreateTrackedObject("BrokenComponentHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            messagingComponent._registeredListeners[listener] = null;

            try
            {
                IReadOnlyList<ComponentMonitorEntry> components =
                    DxMessagingMessageMonitorWindow.CaptureComponentSnapshots(
                        new[] { messagingComponent }
                    );

                Assert.That(components.Count, Is.EqualTo(1));
                Assert.That(components[0].HierarchyPath, Is.EqualTo("BrokenComponentHost"));
                Assert.That(components[0].WarningText, Does.Contain("Diagnostics capture failed"));
                Assert.That(components[0].ProviderStatusText, Does.Contain("unavailable"));
            }
            finally
            {
                messagingComponent._registeredListeners.Clear();
            }
        }

        [Test]
        public void CreateExportTextIncludesVisibleSnapshotEntries()
        {
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(42));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                snapshot.Entries
            );

            Assert.That(exportText, Does.Contain("\"diagnosticsEnabled\": true"));
            Assert.That(exportText, Does.Contain("\"capacity\": 8"));
            Assert.That(exportText, Does.Contain(nameof(NewerMessage)));
            Assert.That(exportText, Does.Contain("42"));
        }

        [Test]
        public void CreateExportTextFiltersVisibleSnapshotEntries()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(42));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                DxMessagingMessageMonitorWindow.FilterEntries(
                    snapshot.Entries,
                    new MessageMonitorViewState(nameof(NewerMessage))
                )
            );

            Assert.That(exportText, Does.Contain("\"entryCount\": 1"));
            Assert.That(exportText, Does.Contain(nameof(NewerMessage)));
            Assert.That(exportText, Does.Not.Contain(nameof(OlderMessage)));
        }

        [Test]
        public void CreateExportTextEscapesJsonStringValues()
        {
            MessageMonitorEntry entry = new(
                "Quote\"Message",
                "Context: slash\\line\nnext",
                "Stack\tTrace\r\u0001"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                snapshot.Entries
            );

            Assert.That(exportText, Does.Contain("Quote\\\"Message"));
            Assert.That(exportText, Does.Contain("slash\\\\line\\nnext"));
            Assert.That(exportText, Does.Contain("Stack\\tTrace\\r\\u0001"));
            Assert.That(exportText, Does.Not.Contain("\u0001"));
        }

        [Test]
        public void CaptureSnapshotReadsDefaultMessageBusHistory()
        {
            MessageBus messageBus = new() { DiagnosticsMode = true };
            messageBus._emissionBuffer.Add(new MessageEmissionData(new OlderMessage()));
            messageBus._emissionBuffer.Add(
                new MessageEmissionData(new NewerMessage(), new InstanceId(42))
            );

            MessageMonitorSnapshot snapshot = DxMessagingMessageMonitorWindow.CaptureSnapshot(
                messageBus
            );

            Assert.That(snapshot.DiagnosticsEnabled, Is.True);
            Assert.That(snapshot.Capacity, Is.EqualTo(IMessageBus.GlobalMessageBufferSize));
            Assert.That(snapshot.Entries.Count, Is.EqualTo(2));
            Assert.That(snapshot.Entries[0].MessageTypeName, Is.EqualTo(nameof(NewerMessage)));
            Assert.That(snapshot.Entries[0].ContextText, Does.Contain("42"));
            Assert.That(snapshot.Entries[1].MessageTypeName, Is.EqualTo(nameof(OlderMessage)));
        }

        private static MessageMonitorEntry CreateEntry(IMessage message, InstanceId? context)
        {
            return MessageMonitorEntry.FromEmission(new MessageEmissionData(message, context));
        }

        private static void AssertTaxonomyRow(VisualElement row, string expectedKind)
        {
            Label kind = row.Q<Label>(DxMessagingMessageMonitorWindow.RouteKindLabelName);
            Assert.That(kind, Is.Not.Null);
            Assert.That(kind.text, Is.EqualTo(expectedKind));
            Assert.That(row.ClassListContains(DxMessagingEditorTheme.RowClassName), Is.True);
            VisualElement dot = row.Query<VisualElement>(
                    className: DxMessagingEditorTheme.DotClassName
                )
                .First();
            Assert.That(dot, Is.Not.Null);
            Assert.That(dot.ClassListContains(ExpectedDotClass(expectedKind)), Is.True);
        }

        private static string ExpectedDotClass(string routeKind)
        {
            switch (routeKind)
            {
                case DxMessagingEditorPalette.UntargetedKind:
                    return DxMessagingEditorTheme.DotUntargetedClassName;
                case DxMessagingEditorPalette.TargetedKind:
                    return DxMessagingEditorTheme.DotTargetedClassName;
                case DxMessagingEditorPalette.BroadcastKind:
                    return DxMessagingEditorTheme.DotBroadcastClassName;
                default:
                    return string.Empty;
            }
        }

        private static void AssertCompleteBorder(VisualElement element, Color expectedColor)
        {
            Assert.That(
                element.style.borderTopWidth.value,
                Is.EqualTo(DxMessagingEditorTheme.CompleteBorderWidth)
            );
            Assert.That(
                element.style.borderRightWidth.value,
                Is.EqualTo(DxMessagingEditorTheme.CompleteBorderWidth)
            );
            Assert.That(
                element.style.borderBottomWidth.value,
                Is.EqualTo(DxMessagingEditorTheme.CompleteBorderWidth)
            );
            Assert.That(
                element.style.borderLeftWidth.value,
                Is.EqualTo(DxMessagingEditorTheme.CompleteBorderWidth)
            );
            AssertColor(element.style.borderTopColor.value, expectedColor);
            AssertColor(element.style.borderRightColor.value, expectedColor);
            AssertColor(element.style.borderBottomColor.value, expectedColor);
            AssertColor(element.style.borderLeftColor.value, expectedColor);
        }

        private static string ExpectedTypeBadgeClass(string routeKind)
        {
            switch (routeKind)
            {
                case DxMessagingEditorPalette.UntargetedKind:
                    return DxMessagingEditorTheme.TypeBadgeUntargetedClassName;
                case DxMessagingEditorPalette.TargetedKind:
                    return DxMessagingEditorTheme.TypeBadgeTargetedClassName;
                case DxMessagingEditorPalette.BroadcastKind:
                    return DxMessagingEditorTheme.TypeBadgeBroadcastClassName;
                default:
                    return string.Empty;
            }
        }

        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.001f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.001f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.001f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.001f));
        }

        private static void SendClick(VisualElement element)
        {
            Assert.That(element, Is.Not.Null, "Cannot click a missing visual element.");
            using (ClickEvent click = ClickEvent.GetPooled())
            {
                click.target = element;
                element.SendEvent(click);
            }
        }

        private static int CountMessageBusRegistrations(IMessageBus messageBus)
        {
            return messageBus.RegisteredUntargeted
                + messageBus.RegisteredTargeted
                + messageBus.RegisteredBroadcast
                + messageBus.RegisteredInterceptors
                + messageBus.RegisteredPostProcessors
                + messageBus.RegisteredGlobalAcceptAll;
        }

        private GameObject CreateTrackedObject(string name)
        {
            GameObject gameObject = new(name);
            _createdObjects.Add(gameObject);
            return gameObject;
        }

        private T CreateTrackedObject<T>(T unityObject)
            where T : Object
        {
            if (unityObject != null)
            {
                _createdObjects.Add(unityObject);
            }
            return unityObject;
        }

        private EditorWindow CreateTrackedEditorWindow()
        {
            EditorWindow window = EditorWindowTestUtility.CreateWindow();
            _createdWindows.Add(window);
            return window;
        }

        /// <summary>The three states a captured context can be in by the time it is rendered.</summary>
        public enum ContextState
        {
            Alive,
            Destroyed,
            NeverCaptured,
        }

        private readonly struct OlderMessage : IUntargetedMessage { }

        private readonly struct NewerMessage : ITargetedMessage { }

        private readonly struct BroadcastMessage : IBroadcastMessage { }

        private static class CollisionOne
        {
            internal readonly struct DuplicateMessage : IUntargetedMessage<DuplicateMessage> { }
        }

        private static class CollisionTwo
        {
            internal readonly struct DuplicateMessage : IUntargetedMessage<DuplicateMessage> { }
        }

        private sealed class TestListener : MonoBehaviour
        {
            public void OnOlderMessage(ref OlderMessage message) { }
        }

        private sealed class ThrowingScriptableMessageBusProvider : ScriptableMessageBusProvider
        {
            public int ResolveCount { get; private set; }

            public override IMessageBus Resolve()
            {
                ResolveCount++;
                throw new InvalidOperationException("Provider resolution should not run here.");
            }
        }
    }
}
#endif
