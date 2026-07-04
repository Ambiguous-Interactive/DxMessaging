#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Analyzers;
    using DxMessaging.Editor.CustomEditors;
    using DxMessaging.Editor.Settings;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEditor.UIElements;
    using UnityEngine;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    [TestFixture]
    public sealed class MessageAwareComponentFallbackEditorTests
    {
        public enum OverlayTargetScenario
        {
            NullObject,
            GameObject,
            Transform,
        }

        private readonly List<Object> _createdObjects = new();
        private readonly List<UnityEditor.Editor> _createdEditors = new();
        private readonly List<EditorWindow> _createdWindows = new();
        private bool _previousBaseCallCheckEnabled;
        private bool _baseCallCheckOverridden;

        [SetUp]
        public void SetUp()
        {
            // Disable diagnostic noise so the overlay's BuildAndRenderOverlay returns early via
            // the gating phase (no EditorGUILayout calls). Stale entries from a previous session
            // could otherwise drive the body into shape != 0 and pollute the body assertion.
            //
            // We reset the override flag BEFORE the throwing call so that if GetOrCreateSettings
            // throws, TearDown sees no override and skips restoration. We capture the previous
            // value into the field BEFORE marking the override as active, so a throw on the
            // capture or the subsequent write still leaves TearDown with the correct
            // captured-vs-overridden state.
            _baseCallCheckOverridden = false;
            DxMessagingSettings settings = DxMessagingSettings.GetOrCreateSettings();
            _previousBaseCallCheckEnabled = settings._baseCallCheckEnabled;
            _baseCallCheckOverridden = true;
            settings._baseCallCheckEnabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            // Destroying inspector editors/objects below repaints the inspector, which in
            // -nographics CI logs benign "No graphic device is available" errors. Unity
            // resets LogAssert tolerance per phase, so re-assert it for the teardown phase
            // (headless only; graphics runs keep full strictness).
            EditorWindowTestUtility.SuppressHeadlessWindowRenderErrors();
            MessageAwareComponentInspectorOverlay.ResetTestSeams();

            foreach (UnityEditor.Editor editor in _createdEditors)
            {
                if (editor != null)
                {
                    Object.DestroyImmediate(editor);
                }
            }
            _createdEditors.Clear();

            EditorWindowTestUtility.CloseTrackedWindows(_createdWindows);

            foreach (Object instance in _createdObjects)
            {
                if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }
            }
            _createdObjects.Clear();

            if (_baseCallCheckOverridden)
            {
                DxMessagingSettings settings = DxMessagingSettings.GetOrCreateSettings();
                settings._baseCallCheckEnabled = _previousBaseCallCheckEnabled;
                _baseCallCheckOverridden = false;
            }
        }

        [Test]
        public void FallbackEditorMustRegisterAsPrimaryNonFallbackEditorForChildClasses()
        {
            // The [CustomEditor] attribute MUST register this editor as a PRIMARY (non-fallback)
            // editor for every MessageAwareComponent subclass. Earlier attempts to use
            // isFallback = true caused Unity to skip our editor entirely and pick GenericInspector
            // instead; which dropped the missing-base-call HelpBox warnings on every component
            // because Unity 2021's Editor.finishedDefaultHeaderGUI hook does not reliably fire for
            // MonoBehaviour subclasses that have no registered [CustomEditor].
            //
            // The "empty vertical gap below the header" bug that motivated the isFallback attempt
            // is solved orthogonally: OnInspectorGUI calls Editor.DrawDefaultInspector(), so the
            // body matches Unity's GenericInspector exactly (including the disabled "Script" row
            // every MonoBehaviour shows). There is no missing row to leave a gap.
            //
            // CustomEditor.isFallback has been a public field on UnityEditor.CustomEditor since
            // at least Unity 2017.2; we read it directly without reflection. The contract:
            // isFallback MUST be false (default), editorForChildClasses MUST be true.
            Type fallbackType = typeof(MessageAwareComponentFallbackEditor);
            object[] attributes = fallbackType.GetCustomAttributes(
                typeof(CustomEditor),
                inherit: false
            );
            Assert.That(
                attributes.Length,
                Is.EqualTo(1),
                "MessageAwareComponentFallbackEditor must declare exactly one [CustomEditor] attribute."
            );

            CustomEditor customEditor = (CustomEditor)attributes[0];
            Assert.That(
                customEditor.isFallback,
                Is.False,
                "MessageAwareComponentFallbackEditor must register with isFallback = false (the default). Setting isFallback = true causes Unity to prefer GenericInspector for every MessageAwareComponent subclass, which silently drops the missing-base-call HelpBox warnings; the regression this test was added to prevent."
            );

            FieldInfo editorForChildClassesField = typeof(CustomEditor).GetField(
                "m_EditorForChildClasses",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                editorForChildClassesField,
                Is.Not.Null,
                "Unity's CustomEditor.m_EditorForChildClasses field is missing; Unity may have renamed the field; update this test."
            );
            bool editorForChildClasses = (bool)editorForChildClassesField.GetValue(customEditor);
            Assert.That(
                editorForChildClasses,
                Is.True,
                "MessageAwareComponentFallbackEditor must register with editorForChildClasses: true so that ALL MessageAwareComponent subclasses get the warning HelpBox."
            );
        }

        [Test]
        public void FallbackEditorIsSelectedForSubclassWithoutCustomEditor()
        {
            // End-to-end check: Unity must select our editor for MessageAwareComponent
            // subclasses that have no user-defined [CustomEditor]. With isFallback = false and
            // editorForChildClasses = true, our editor is the most-specific match for any
            // MessageAwareComponent subclass that has no dedicated user editor.
            GameObject host = CreateTrackedObject("FallbackEditorSelectionHost");
            EmptyMessageAwareComponentForFallbackTest component =
                host.AddComponent<EmptyMessageAwareComponentForFallbackTest>();
            Assert.That(component, Is.Not.Null, "Failed to attach test subclass to host.");

            UnityEditor.Editor editor = UnityEditor.Editor.CreateEditor(component);
            _createdEditors.Add(editor);

            Assert.That(
                editor,
                Is.Not.Null,
                "Editor.CreateEditor returned null for the empty subclass; Unity could not resolve any editor."
            );
            Assert.That(
                editor,
                Is.InstanceOf<MessageAwareComponentFallbackEditor>(),
                "Unity must select MessageAwareComponentFallbackEditor for a MessageAwareComponent subclass with no user-defined [CustomEditor]."
            );
        }

        [TestCase(typeof(EmptyMessageAwareComponentForFallbackTest))]
        [TestCase(typeof(SerializedFieldMessageAwareComponentForFallbackTest))]
        public void OverlayDoesNotRenderWhenBaseCallCheckIsDisabled(Type componentType)
        {
            // This test intentionally avoids calling Editor.OnInspectorGUI directly: invoking
            // DrawDefaultInspector() outside Unity's active IMGUI cycle throws inside
            // GUILayoutUtility. Instead we assert the overlay body itself short-circuits with
            // shape == 0 (returns false, emits no UI) when the base-call check is disabled.
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                $"FallbackEditorBodyHost_{componentType.Name}",
                componentType
            );

            bool rendered = InvokeBuildAndRenderOverlay(component);
            Assert.That(
                rendered,
                Is.False,
                "BuildAndRenderOverlay must return false (render nothing) when base-call checks are disabled. "
                    + $"ComponentType={componentType.FullName}; GUI context: {DescribeCurrentGuiContext()}."
            );

            Assert.DoesNotThrow(
                () => MessageAwareComponentInspectorOverlay.RenderInsideOnInspectorGUI(component),
                "RenderInsideOnInspectorGUI must remain a no-op when overlay rendering is gated off. "
                    + $"ComponentType={componentType.FullName}; GUI context: {DescribeCurrentGuiContext()}."
            );
        }

        [TestCase(OverlayTargetScenario.NullObject)]
        [TestCase(OverlayTargetScenario.GameObject)]
        [TestCase(OverlayTargetScenario.Transform)]
        public void RenderInsideOnInspectorGUIGracefullyNoOpsForUnsupportedTargets(
            OverlayTargetScenario scenario
        )
        {
            Object target = CreateTargetForScenario(scenario);

            Assert.DoesNotThrow(
                () => MessageAwareComponentInspectorOverlay.RenderInsideOnInspectorGUI(target),
                "RenderInsideOnInspectorGUI must no-op for unsupported targets rather than throwing. "
                    + $"Scenario={scenario}; GUI context: {DescribeCurrentGuiContext()}."
            );
        }

        [Test]
        public void ResolveInspectorStateReturnsNoneForUnsupportedTargets()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);

            foreach (
                OverlayTargetScenario scenario in Enum.GetValues(typeof(OverlayTargetScenario))
            )
            {
                Object target = CreateTargetForScenario(scenario);

                MessageAwareComponentInspectorState state =
                    MessageAwareComponentInspectorOverlay.ResolveInspectorState(
                        target,
                        settings,
                        harvesterAvailable: true,
                        isFreshThisSession: true,
                        entry: CreateReportEntry("Awake")
                    );

                Assert.That(
                    state.Kind,
                    Is.EqualTo(MessageAwareComponentInspectorStateKind.None),
                    $"Scenario={scenario}"
                );
            }
        }

        [Test]
        public void ResolveInspectorStateReturnsNoneWhenBaseCallCheckIsDisabled()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: false);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorStateDisabledHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );

            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorOverlay.ResolveInspectorState(
                    component,
                    settings,
                    harvesterAvailable: true,
                    isFreshThisSession: true,
                    entry: CreateReportEntry("Awake")
                );

            Assert.That(state.Kind, Is.EqualTo(MessageAwareComponentInspectorStateKind.None));
        }

        [Test]
        public void ResolveInspectorStateReturnsHarvesterUnavailableBeforeIgnoredType()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorStateUnavailableHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            string fullName = GetOverlayTypeName(component);
            settings._baseCallIgnoredTypes.Add(fullName);

            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorOverlay.ResolveInspectorState(
                    component,
                    settings,
                    harvesterAvailable: false,
                    isFreshThisSession: true,
                    entry: null
                );

            Assert.That(
                state.Kind,
                Is.EqualTo(MessageAwareComponentInspectorStateKind.HarvesterUnavailable)
            );
            Assert.That(state.FullName, Is.EqualTo(fullName));
            Assert.That(state.Component, Is.SameAs(component));
            Assert.That(state.Settings, Is.SameAs(settings));
        }

        [Test]
        public void ResolveInspectorStateReturnsIgnoredType()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorStateIgnoredHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            string fullName = GetOverlayTypeName(component);
            settings._baseCallIgnoredTypes.Add(fullName);

            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorOverlay.ResolveInspectorState(
                    component,
                    settings,
                    harvesterAvailable: true,
                    isFreshThisSession: true,
                    entry: CreateReportEntry("Awake")
                );

            Assert.That(
                state.Kind,
                Is.EqualTo(MessageAwareComponentInspectorStateKind.IgnoredType)
            );
            Assert.That(state.FullName, Is.EqualTo(fullName));
            Assert.That(state.Component, Is.SameAs(component));
            Assert.That(state.Settings, Is.SameAs(settings));
        }

        [Test]
        public void ResolveInspectorStateReturnsMissingBaseWarningWithFreshnessFlag()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorStateWarningHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            BaseCallReportEntry entry = CreateReportEntry("Awake", "OnEnable");

            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorOverlay.ResolveInspectorState(
                    component,
                    settings,
                    harvesterAvailable: true,
                    isFreshThisSession: false,
                    entry: entry
                );

            Assert.That(
                state.Kind,
                Is.EqualTo(MessageAwareComponentInspectorStateKind.MissingBaseCallWarning)
            );
            Assert.That(state.Entry, Is.SameAs(entry));
            Assert.That(state.IsFreshThisSession, Is.False);
            Assert.That(state.FullName, Is.EqualTo(GetOverlayTypeName(component)));
        }

        [Test]
        public void ResolveInspectorStateReturnsNoneForEmptyReportEntry()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorStateEmptyReportHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );

            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorOverlay.ResolveInspectorState(
                    component,
                    settings,
                    harvesterAvailable: true,
                    isFreshThisSession: true,
                    entry: new BaseCallReportEntry()
                );

            Assert.That(state.Kind, Is.EqualTo(MessageAwareComponentInspectorStateKind.None));
        }

        [Test]
        public void ResolveInspectorStateReturnsNoneForDestroyedMessageAwareComponent()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorStateDestroyedComponentHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );

            Object.DestroyImmediate(component);

            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorOverlay.ResolveInspectorState(
                    component,
                    settings,
                    harvesterAvailable: false,
                    isFreshThisSession: true,
                    entry: CreateReportEntry("Awake")
                );

            Assert.That(state.Kind, Is.EqualTo(MessageAwareComponentInspectorStateKind.None));
        }

        [Test]
        public void InspectorViewReturnsEmptyRootForNoneState()
        {
            VisualElement root = MessageAwareComponentInspectorView.Create(
                MessageAwareComponentInspectorState.None,
                MessageAwareComponentInspectorViewActions.None
            );

            Assert.That(root.name, Is.EqualTo(MessageAwareComponentInspectorView.RootName));
            Assert.That(
                root.ClassListContains(MessageAwareComponentInspectorView.RootClassName),
                Is.True
            );
            Assert.That(
                root.ClassListContains(MessageAwareComponentInspectorView.StateNoneClassName),
                Is.True
            );
            Assert.That(root.childCount, Is.EqualTo(0));
        }

        [Test]
        public void InspectorViewRendersHarvesterUnavailableState()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorViewUnavailableHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForHarvesterUnavailable(
                    component,
                    settings,
                    GetOverlayTypeName(component),
                    isFreshThisSession: true
                );

            VisualElement root = MessageAwareComponentInspectorView.Create(
                state,
                MessageAwareComponentInspectorViewActions.None
            );

            Assert.That(
                root.ClassListContains(MessageAwareComponentInspectorView.StateInfoClassName),
                Is.True
            );
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.ThemeClassName), Is.True);
            Assert.That(
                root.ClassListContains(DxMessagingEditorTheme.AdmonitionClassName),
                Is.True
            );
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.NoteClassName), Is.True);
            Assert.That(
                root.Query<VisualElement>(className: "dxmessaging-inspector-warning__rail")
                    .ToList(),
                Is.Empty
            );
            AssertCompleteBorder(root, DxMessagingEditorPalette.Untargeted);
            Assert.That(
                root.Q<Label>(MessageAwareComponentInspectorView.TitleLabelName).text,
                Does.Contain("Inspector overlay unavailable")
            );
            Assert.That(
                root.Q<Label>(MessageAwareComponentInspectorView.BodyLabelName).text,
                Does.Contain("DXMSG006")
            );
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.OpenScriptButtonName),
                Is.Null
            );
        }

        [Test]
        public void InspectorViewRendersIgnoredStateWithStopIgnoringDelegate()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorViewIgnoredHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            string fullName = GetOverlayTypeName(component);
            settings._baseCallIgnoredTypes.Add(fullName);
            int stopIgnoringCalls = 0;
            MessageAwareComponentInspectorState observedState =
                MessageAwareComponentInspectorState.None;
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForIgnoredType(
                    component,
                    settings,
                    fullName,
                    isFreshThisSession: true
                );

            VisualElement root = MessageAwareComponentInspectorView.Create(
                state,
                new MessageAwareComponentInspectorViewActions(
                    onOpenScript: null,
                    onIgnoreType: null,
                    onStopIgnoring: nextState =>
                    {
                        stopIgnoringCalls++;
                        observedState = nextState;
                    }
                )
            );
            AttachToShownWindow(root);

            Button stopIgnoring = root.Q<Button>(
                MessageAwareComponentInspectorView.StopIgnoringButtonName
            );
            Assert.That(stopIgnoring, Is.Not.Null);
            Assert.That(stopIgnoring.text, Is.EqualTo("Stop ignoring"));
            Assert.That(
                root.Q<Label>(MessageAwareComponentInspectorView.BodyLabelName).text,
                Does.Contain(fullName)
            );

            SendClick(stopIgnoring);

            Assert.That(stopIgnoringCalls, Is.EqualTo(1));
            Assert.That(observedState.FullName, Is.EqualTo(fullName));
            Assert.That(settings._baseCallIgnoredTypes, Is.EquivalentTo(new[] { fullName }));
        }

        [Test]
        public void InspectorViewRendersMissingBaseWarningWithMethodsAndStaleNote()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorViewWarningHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                    component,
                    settings,
                    GetOverlayTypeName(component),
                    CreateReportEntry("Awake", "OnEnable"),
                    isFreshThisSession: false
                );

            VisualElement root = MessageAwareComponentInspectorView.Create(
                state,
                MessageAwareComponentInspectorViewActions.None
            );

            Assert.That(
                root.ClassListContains(MessageAwareComponentInspectorView.StateWarningClassName),
                Is.True
            );
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.ThemeClassName), Is.True);
            Assert.That(
                root.ClassListContains(DxMessagingEditorTheme.AdmonitionClassName),
                Is.True
            );
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.WarningClassName), Is.True);
            AssertCompleteBorder(root, DxMessagingEditorPalette.Amber);
            Assert.That(
                root.Q<Label>(MessageAwareComponentInspectorView.TitleLabelName).text,
                Does.Contain("Missing MessageAwareComponent base calls")
            );
            Assert.That(
                GetMethodListTexts(root),
                Is.EqualTo(
                    new[]
                    {
                        CreateExpectedMethodRow("Awake", GetOverlayTypeName(component)),
                        CreateExpectedMethodRow("OnEnable", GetOverlayTypeName(component)),
                    }
                )
            );
            Assert.That(
                root.Q<Label>(MessageAwareComponentInspectorView.StaleCacheNoteName).text,
                Does.Contain("cached from previous session")
            );
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.OpenScriptButtonName),
                Is.Not.Null
            );
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.IgnoreTypeButtonName),
                Is.Not.Null
            );
        }

        [Test]
        public void InspectorViewWarningButtonsCallDelegatesWithoutMutatingSettings()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorViewWarningActionsHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            string fullName = GetOverlayTypeName(component);
            int openCalls = 0;
            int ignoreCalls = 0;
            int stopIgnoringCalls = 0;
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                    component,
                    settings,
                    fullName,
                    CreateReportEntry("Awake"),
                    isFreshThisSession: true
                );
            MessageAwareComponentInspectorViewActions actions = new(
                onOpenScript: observed =>
                {
                    openCalls++;
                    Assert.That(observed.FullName, Is.EqualTo(fullName));
                },
                onIgnoreType: observed =>
                {
                    ignoreCalls++;
                    Assert.That(observed.FullName, Is.EqualTo(fullName));
                },
                onStopIgnoring: _ => stopIgnoringCalls++
            );

            VisualElement root = MessageAwareComponentInspectorView.Create(state, actions);
            AttachToShownWindow(root);
            Button openScript = root.Q<Button>(
                MessageAwareComponentInspectorView.OpenScriptButtonName
            );
            Button ignoreType = root.Q<Button>(
                MessageAwareComponentInspectorView.IgnoreTypeButtonName
            );

            SendClick(openScript);
            SendClick(ignoreType);

            Assert.That(openCalls, Is.EqualTo(1));
            Assert.That(ignoreCalls, Is.EqualTo(1));
            Assert.That(stopIgnoringCalls, Is.EqualTo(0));
            Assert.That(settings._baseCallIgnoredTypes, Is.Empty);
            Assert.That(
                root.Q<Label>(MessageAwareComponentInspectorView.StaleCacheNoteName),
                Is.Null
            );
        }

        [Test]
        public void InspectorViewDisablesActionButtonsWhenDelegatesAreMissing()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorViewDisabledActionsHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                    component,
                    settings,
                    GetOverlayTypeName(component),
                    CreateReportEntry("Awake"),
                    isFreshThisSession: true
                );

            VisualElement root = MessageAwareComponentInspectorView.Create(
                state,
                MessageAwareComponentInspectorViewActions.None
            );
            Button openScript = root.Q<Button>(
                MessageAwareComponentInspectorView.OpenScriptButtonName
            );
            Button ignoreType = root.Q<Button>(
                MessageAwareComponentInspectorView.IgnoreTypeButtonName
            );

            Assert.That(openScript, Is.Not.Null);
            Assert.That(ignoreType, Is.Not.Null);
            Assert.That(
                openScript.ClassListContains(DxMessagingEditorTheme.ButtonGhostClassName),
                Is.True
            );
            Assert.That(
                ignoreType.ClassListContains(DxMessagingEditorTheme.ButtonGhostClassName),
                Is.True
            );
            Assert.That(openScript.enabledSelf, Is.False);
            Assert.That(ignoreType.enabledSelf, Is.False);
        }

        [Test]
        public void FallbackEditorCreateInspectorGUIOmitsNoneWarningViewAndHostsDefaultInspectorBody()
        {
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "FallbackEditorCreateInspectorGuiHost",
                typeof(SerializedFieldMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentFallbackEditor editor = CreateFallbackEditor(component);

            VisualElement root = editor.CreateInspectorGUI();

            Assert.That(root, Is.Not.Null);
            Assert.That(root.name, Is.EqualTo(MessageAwareComponentFallbackEditor.RootName));
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.ThemeClassName), Is.True);
            Assert.That(
                root.ClassListContains(MessageAwareComponentFallbackEditor.RootClassName),
                Is.True
            );

            VisualElement warning = root.Q<VisualElement>(
                MessageAwareComponentInspectorView.RootName
            );
            Assert.That(
                warning,
                Is.Null,
                "SetUp disables base-call checks; the fallback UI Toolkit path must not leave a blank warning gap."
            );
            VisualElement warningHost = root.Q<VisualElement>(
                MessageAwareComponentFallbackEditor.WarningHostName
            );
            Assert.That(warningHost, Is.Not.Null);
            Assert.That(warningHost.style.display.value, Is.EqualTo(DisplayStyle.None));

            VisualElement body = root.Q<VisualElement>(
                MessageAwareComponentFallbackEditor.DefaultInspectorBodyName
            );
            Assert.That(body, Is.Not.Null);
            Assert.That(
                body.ClassListContains(
                    MessageAwareComponentFallbackEditor.DefaultInspectorBodyClassName
                ),
                Is.True
            );
            List<string> bindingPaths = body.Query<PropertyField>()
                .ToList()
                .ConvertAll(field => field.bindingPath);
            Assert.That(bindingPaths, Does.Contain("m_Script"));
            Assert.That(bindingPaths, Does.Contain("_value"));
        }

        [Test]
        public void FallbackEditorRefreshInspectorWarningRebuildsRetainedTreeWithoutDuplicates()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "FallbackEditorRetainedRefreshHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentFallbackEditor editor = CreateFallbackEditor(component);
            string fullName = GetOverlayTypeName(component);
            MessageAwareComponentInspectorState currentState =
                MessageAwareComponentInspectorState.None;

            VisualElement root = MessageAwareComponentFallbackEditor.BuildInspectorGUI(
                editor,
                () => currentState
            );
            AttachToShownWindow(root);

            Assert.That(
                root.Q<VisualElement>(MessageAwareComponentInspectorView.RootName),
                Is.Null
            );

            currentState = MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                component,
                settings,
                fullName,
                CreateReportEntry("Awake"),
                isFreshThisSession: true
            );
            MessageAwareComponentFallbackEditor.RefreshInspectorWarning(root);

            Assert.That(
                root.Query<VisualElement>(MessageAwareComponentInspectorView.RootName)
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.IgnoreTypeButtonName),
                Is.Not.Null
            );

            currentState = MessageAwareComponentInspectorState.ForIgnoredType(
                component,
                settings,
                fullName,
                isFreshThisSession: true
            );
            MessageAwareComponentFallbackEditor.RefreshInspectorWarning(root);

            Assert.That(
                root.Query<VisualElement>(MessageAwareComponentInspectorView.RootName)
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.StopIgnoringButtonName),
                Is.Not.Null
            );
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.IgnoreTypeButtonName),
                Is.Null
            );

            currentState = MessageAwareComponentInspectorState.None;
            MessageAwareComponentFallbackEditor.RefreshInspectorWarning(root);

            Assert.That(
                root.Q<VisualElement>(MessageAwareComponentInspectorView.RootName),
                Is.Null
            );
        }

        [Test]
        public void FallbackEditorAttachRefreshesDetachedWarningState()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "FallbackEditorAttachRefreshHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentFallbackEditor editor = CreateFallbackEditor(component);
            string fullName = GetOverlayTypeName(component);
            MessageAwareComponentInspectorState currentState =
                MessageAwareComponentInspectorState.None;
            VisualElement root = MessageAwareComponentFallbackEditor.BuildInspectorGUI(
                editor,
                () => currentState
            );

            currentState = MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                component,
                settings,
                fullName,
                CreateReportEntry("Awake"),
                isFreshThisSession: true
            );
            AttachToShownWindow(root);

            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.IgnoreTypeButtonName),
                Is.Not.Null
            );
        }

        [Test]
        public void FallbackEditorTransientNoneOnAttachRetriesAndRestoresWarningState()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "FallbackEditorAttachTransientRetryHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentFallbackEditor editor = CreateFallbackEditor(component);
            string fullName = GetOverlayTypeName(component);
            MessageAwareComponentInspectorState warningState =
                MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                    component,
                    settings,
                    fullName,
                    CreateReportEntry("Awake"),
                    isFreshThisSession: true
                );
            bool transientlyBlocked = false;
            List<Action> scheduledRetries = new();
            MessageAwareComponentInspectorOverlay.TransientRefreshScheduler = work =>
                scheduledRetries.Add(work);
            MessageAwareComponentInspectorOverlay.InspectorResolutionTransientBlocker = () =>
                transientlyBlocked;
            VisualElement root = MessageAwareComponentFallbackEditor.BuildInspectorGUI(
                editor,
                () => transientlyBlocked ? MessageAwareComponentInspectorState.None : warningState
            );

            AttachToShownWindow(root);

            Button initialIgnoreType = root.Q<Button>(
                MessageAwareComponentInspectorView.IgnoreTypeButtonName
            );
            Assert.That(initialIgnoreType, Is.Not.Null);

            transientlyBlocked = true;
            MessageAwareComponentFallbackEditor.RefreshInspectorWarning(root);

            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.IgnoreTypeButtonName),
                Is.SameAs(initialIgnoreType)
            );
            Assert.That(scheduledRetries.Count, Is.EqualTo(1));

            transientlyBlocked = false;
            scheduledRetries[0].Invoke();

            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.IgnoreTypeButtonName),
                Is.Not.Null
            );
        }

        [Test]
        public void FallbackEditorIgnoreActionRefreshesRetainedWarningAfterDeferredMutation()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "FallbackEditorDeferredIgnoreRefreshHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentFallbackEditor editor = CreateFallbackEditor(component);
            string fullName = GetOverlayTypeName(component);
            BaseCallReportEntry entry = CreateReportEntry("Awake");
            List<Action> scheduledMutations = new();
            MessageAwareComponentInspectorOverlay.AssetDatabaseMutationScheduler = work =>
                scheduledMutations.Add(work);
            VisualElement root = MessageAwareComponentFallbackEditor.BuildInspectorGUI(
                editor,
                () =>
                    settings._baseCallIgnoredTypes.Contains(fullName)
                        ? MessageAwareComponentInspectorState.ForIgnoredType(
                            component,
                            settings,
                            fullName,
                            isFreshThisSession: true
                        )
                        : MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                            component,
                            settings,
                            fullName,
                            entry,
                            isFreshThisSession: true
                        )
            );
            AttachToShownWindow(root);
            Button ignoreType = root.Q<Button>(
                MessageAwareComponentInspectorView.IgnoreTypeButtonName
            );

            SendClick(ignoreType);

            Assert.That(settings._baseCallIgnoredTypes, Is.Empty);
            Assert.That(scheduledMutations.Count, Is.EqualTo(1));

            EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(scheduledMutations[0].Invoke);

            Assert.That(settings._baseCallIgnoredTypes, Is.EquivalentTo(new[] { fullName }));
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.StopIgnoringButtonName),
                Is.Not.Null
            );
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.IgnoreTypeButtonName),
                Is.Null
            );
            Assert.That(
                root.Query<VisualElement>(MessageAwareComponentInspectorView.RootName)
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
        }

        [Test]
        public void FallbackEditorStopIgnoringActionRefreshesRetainedWarningAfterDeferredMutation()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "FallbackEditorDeferredStopIgnoringRefreshHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentFallbackEditor editor = CreateFallbackEditor(component);
            string fullName = GetOverlayTypeName(component);
            settings._baseCallIgnoredTypes.Add(fullName);
            BaseCallReportEntry entry = CreateReportEntry("Awake");
            List<Action> scheduledMutations = new();
            MessageAwareComponentInspectorOverlay.AssetDatabaseMutationScheduler = work =>
                scheduledMutations.Add(work);
            VisualElement root = MessageAwareComponentFallbackEditor.BuildInspectorGUI(
                editor,
                () =>
                    settings._baseCallIgnoredTypes.Contains(fullName)
                        ? MessageAwareComponentInspectorState.ForIgnoredType(
                            component,
                            settings,
                            fullName,
                            isFreshThisSession: true
                        )
                        : MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                            component,
                            settings,
                            fullName,
                            entry,
                            isFreshThisSession: true
                        )
            );
            AttachToShownWindow(root);
            Button stopIgnoring = root.Q<Button>(
                MessageAwareComponentInspectorView.StopIgnoringButtonName
            );

            SendClick(stopIgnoring);

            Assert.That(settings._baseCallIgnoredTypes, Is.EquivalentTo(new[] { fullName }));
            Assert.That(scheduledMutations.Count, Is.EqualTo(1));

            EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(scheduledMutations[0].Invoke);

            Assert.That(settings._baseCallIgnoredTypes, Is.Empty);
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.IgnoreTypeButtonName),
                Is.Not.Null
            );
            Assert.That(
                root.Q<Button>(MessageAwareComponentInspectorView.StopIgnoringButtonName),
                Is.Null
            );
            Assert.That(
                root.Query<VisualElement>(MessageAwareComponentInspectorView.RootName)
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
        }

        [Test]
        public void OverlayInspectorViewIgnoreActionDefersAndDrainsSettingsMutation()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorViewDeferredIgnoreHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            string fullName = GetOverlayTypeName(component);
            List<Action> scheduledMutations = new();
            MessageAwareComponentInspectorOverlay.AssetDatabaseMutationScheduler = work =>
                scheduledMutations.Add(work);
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                    component,
                    settings,
                    fullName,
                    CreateReportEntry("Awake"),
                    isFreshThisSession: true
                );

            VisualElement root = MessageAwareComponentInspectorOverlay.CreateInspectorView(state);
            AttachToShownWindow(root);
            Button ignoreType = root.Q<Button>(
                MessageAwareComponentInspectorView.IgnoreTypeButtonName
            );

            Assert.That(ignoreType, Is.Not.Null);
            Assert.That(ignoreType.enabledSelf, Is.True);

            SendClick(ignoreType);

            Assert.That(settings._baseCallIgnoredTypes, Is.Empty);
            Assert.That(scheduledMutations.Count, Is.EqualTo(1));

            EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(scheduledMutations[0].Invoke);

            Assert.That(settings._baseCallIgnoredTypes, Is.EquivalentTo(new[] { fullName }));
        }

        [Test]
        public void OverlayInspectorViewIgnoreActionInvalidationRequestsInspectorRepaint()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorViewDeferredIgnoreRepaintHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            string fullName = GetOverlayTypeName(component);
            List<Action> scheduledMutations = new();
            int repaintRequests = 0;
            MessageAwareComponentInspectorOverlay.AssetDatabaseMutationScheduler = work =>
                scheduledMutations.Add(work);
            MessageAwareComponentInspectorOverlay.InspectorRepaintAction = () => repaintRequests++;
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                    component,
                    settings,
                    fullName,
                    CreateReportEntry("Awake"),
                    isFreshThisSession: true
                );

            VisualElement root = MessageAwareComponentInspectorOverlay.CreateInspectorView(state);
            AttachToShownWindow(root);
            Button ignoreType = root.Q<Button>(
                MessageAwareComponentInspectorView.IgnoreTypeButtonName
            );

            SendClick(ignoreType);
            EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(scheduledMutations[0].Invoke);

            Assert.That(repaintRequests, Is.EqualTo(1));
        }

        [Test]
        public void OverlayInspectorViewStopIgnoringActionDefersAndDrainsSettingsMutation()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorViewDeferredStopIgnoringHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            string fullName = GetOverlayTypeName(component);
            settings._baseCallIgnoredTypes.Add(fullName);
            List<Action> scheduledMutations = new();
            MessageAwareComponentInspectorOverlay.AssetDatabaseMutationScheduler = work =>
                scheduledMutations.Add(work);
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForIgnoredType(
                    component,
                    settings,
                    fullName,
                    isFreshThisSession: true
                );

            VisualElement root = MessageAwareComponentInspectorOverlay.CreateInspectorView(state);
            AttachToShownWindow(root);
            Button stopIgnoring = root.Q<Button>(
                MessageAwareComponentInspectorView.StopIgnoringButtonName
            );

            Assert.That(stopIgnoring, Is.Not.Null);
            Assert.That(stopIgnoring.enabledSelf, Is.True);

            SendClick(stopIgnoring);

            Assert.That(settings._baseCallIgnoredTypes, Is.EquivalentTo(new[] { fullName }));
            Assert.That(scheduledMutations.Count, Is.EqualTo(1));

            EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(scheduledMutations[0].Invoke);

            Assert.That(settings._baseCallIgnoredTypes, Is.Empty);
        }

        [Test]
        public void OverlayInspectorViewOpenScriptActionUsesStateComponentAndEntry()
        {
            DxMessagingSettings settings = CreateTransientSettings(baseCallCheckEnabled: true);
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "InspectorViewOpenScriptActionHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            BaseCallReportEntry entry = CreateReportEntry("Awake");
            entry.line = 42;
            int openCalls = 0;
            MessageAwareComponent observedComponent = null;
            BaseCallReportEntry observedEntry = null;
            MessageAwareComponentInspectorOverlay.OpenScriptAction = (nextComponent, nextEntry) =>
            {
                openCalls++;
                observedComponent = nextComponent;
                observedEntry = nextEntry;
            };
            MessageAwareComponentInspectorState state =
                MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                    component,
                    settings,
                    GetOverlayTypeName(component),
                    entry,
                    isFreshThisSession: true
                );

            VisualElement root = MessageAwareComponentInspectorOverlay.CreateInspectorView(state);
            AttachToShownWindow(root);
            Button openScript = root.Q<Button>(
                MessageAwareComponentInspectorView.OpenScriptButtonName
            );

            SendClick(openScript);

            Assert.That(openCalls, Is.EqualTo(1));
            Assert.That(observedComponent, Is.SameAs(component));
            Assert.That(observedEntry, Is.SameAs(entry));
        }

        [Test]
        public void UserDefinedCustomEditorStillTakesPrecedenceForSpecificSubclass()
        {
            GameObject host = CreateTrackedObject("SpecificCustomEditorPrecedenceHost");
            CustomEditorMessageAwareComponentForFallbackTest component =
                host.AddComponent<CustomEditorMessageAwareComponentForFallbackTest>();

            UnityEditor.Editor editor = UnityEditor.Editor.CreateEditor(component);
            _createdEditors.Add(editor);

            Assert.That(
                editor,
                Is.InstanceOf<SpecificMessageAwareComponentCustomEditorForFallbackTest>()
            );
        }

        [Test]
        public void HeaderHookSkipsPackageFallbackEditorToAvoidDoubleRender()
        {
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "HeaderHookFallbackSkipHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentFallbackEditor editor = CreateFallbackEditor(component);

            bool shouldRenderHeaderHook =
                MessageAwareComponentInspectorOverlay.ShouldRenderHeaderHook(editor);

            Assert.That(shouldRenderHeaderHook, Is.False);
        }

        [Test]
        public void HeaderHookRenderSkipsPackageFallbackEditorToAvoidDoubleRender()
        {
            MessageAwareComponent component = CreateTrackedMessageAwareComponent(
                "HeaderHookFallbackRenderSkipHost",
                typeof(EmptyMessageAwareComponentForFallbackTest)
            );
            MessageAwareComponentFallbackEditor editor = CreateFallbackEditor(component);
            int renderCalls = 0;
            MessageAwareComponentInspectorOverlay.ImGuiRenderAction = _ =>
            {
                renderCalls++;
                return true;
            };

            bool rendered = MessageAwareComponentInspectorOverlay.TryRenderHeaderHook(
                editor,
                EventType.Repaint
            );

            Assert.That(rendered, Is.False);
            Assert.That(renderCalls, Is.EqualTo(0));
        }

        [Test]
        public void HeaderHookRemainsEnabledForSpecificUserCustomEditor()
        {
            GameObject host = CreateTrackedObject("HeaderHookSpecificCustomEditorHost");
            CustomEditorMessageAwareComponentForFallbackTest component =
                host.AddComponent<CustomEditorMessageAwareComponentForFallbackTest>();
            UnityEditor.Editor editor = UnityEditor.Editor.CreateEditor(component);
            _createdEditors.Add(editor);
            Assert.That(
                editor,
                Is.InstanceOf<SpecificMessageAwareComponentCustomEditorForFallbackTest>()
            );

            bool shouldRenderHeaderHook =
                MessageAwareComponentInspectorOverlay.ShouldRenderHeaderHook(editor);

            Assert.That(shouldRenderHeaderHook, Is.True);
        }

        [Test]
        public void HeaderHookRenderInvokesOverlayForSpecificUserCustomEditorAndLayoutClearsLatch()
        {
            GameObject host = CreateTrackedObject("HeaderHookSpecificCustomEditorRenderHost");
            CustomEditorMessageAwareComponentForFallbackTest component =
                host.AddComponent<CustomEditorMessageAwareComponentForFallbackTest>();
            UnityEditor.Editor editor = UnityEditor.Editor.CreateEditor(component);
            _createdEditors.Add(editor);
            Assert.That(
                editor,
                Is.InstanceOf<SpecificMessageAwareComponentCustomEditorForFallbackTest>()
            );
            int renderCalls = 0;
            MessageAwareComponent observedComponent = null;
            MessageAwareComponentInspectorOverlay.ImGuiRenderAction = nextComponent =>
            {
                renderCalls++;
                observedComponent = nextComponent;
                return true;
            };

            bool firstRepaintRendered = MessageAwareComponentInspectorOverlay.TryRenderHeaderHook(
                editor,
                EventType.Repaint
            );
            bool duplicateRepaintRendered =
                MessageAwareComponentInspectorOverlay.TryRenderHeaderHook(
                    editor,
                    EventType.Repaint
                );
            bool layoutRendered = MessageAwareComponentInspectorOverlay.TryRenderHeaderHook(
                editor,
                EventType.Layout
            );
            bool secondRepaintRendered = MessageAwareComponentInspectorOverlay.TryRenderHeaderHook(
                editor,
                EventType.Repaint
            );

            Assert.That(firstRepaintRendered, Is.True);
            Assert.That(duplicateRepaintRendered, Is.False);
            Assert.That(layoutRendered, Is.False);
            Assert.That(secondRepaintRendered, Is.True);
            Assert.That(renderCalls, Is.EqualTo(2));
            Assert.That(observedComponent, Is.SameAs(component));
        }

        [Test]
        public void HeaderHookGracefullySkipsNullEditor()
        {
            bool shouldRenderHeaderHook =
                MessageAwareComponentInspectorOverlay.ShouldRenderHeaderHook(null);

            Assert.That(shouldRenderHeaderHook, Is.False);
        }

        private MessageAwareComponent CreateTrackedMessageAwareComponent(string hostName, Type type)
        {
            Assert.That(type, Is.Not.Null, "Component type test input must not be null.");
            Assert.That(
                typeof(MessageAwareComponent).IsAssignableFrom(type),
                Is.True,
                $"Test input type must derive from {nameof(MessageAwareComponent)}. Actual: {type.FullName}."
            );

            GameObject host = CreateTrackedObject(hostName);
            Component component = host.AddComponent(type);
            Assert.That(
                component,
                Is.Not.Null,
                $"Failed to attach {type.FullName} to host GameObject."
            );

            MessageAwareComponent messageAwareComponent = component as MessageAwareComponent;
            Assert.That(
                messageAwareComponent,
                Is.Not.Null,
                $"Attached component must be assignable to {nameof(MessageAwareComponent)}. Actual: {component.GetType().FullName}."
            );
            return messageAwareComponent;
        }

        private MessageAwareComponentFallbackEditor CreateFallbackEditor(
            MessageAwareComponent component
        )
        {
            UnityEditor.Editor editor = UnityEditor.Editor.CreateEditor(component);
            _createdEditors.Add(editor);
            Assert.That(editor, Is.InstanceOf<MessageAwareComponentFallbackEditor>());
            return (MessageAwareComponentFallbackEditor)editor;
        }

        private DxMessagingSettings CreateTransientSettings(bool baseCallCheckEnabled)
        {
            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            settings._baseCallCheckEnabled = baseCallCheckEnabled;
            settings._baseCallIgnoredTypes = new List<string>();
            _createdObjects.Add(settings);
            return settings;
        }

        private static BaseCallReportEntry CreateReportEntry(params string[] missingMethods)
        {
            return new BaseCallReportEntry
            {
                missingBaseFor = new List<string>(missingMethods),
                diagnosticIds = new List<string> { "DXMSG006" },
            };
        }

        private static string GetOverlayTypeName(MessageAwareComponent component)
        {
            return (component.GetType().FullName ?? string.Empty).Replace('+', '.');
        }

        private static List<string> GetMethodListTexts(VisualElement root)
        {
            return root.Q<VisualElement>(MessageAwareComponentInspectorView.MethodListName)
                .Query<Label>()
                .ToList()
                .ConvertAll(label => label.text);
        }

        private static string CreateExpectedMethodRow(string methodName, string fullName)
        {
            return $"{methodName}: {BaseCallTypeScannerCore.GetMissingBaseConsequenceLine(methodName, fullName)}";
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

        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.001f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.001f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.001f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.001f));
        }

        private void AttachToShownWindow(VisualElement root)
        {
            EditorWindow window = EditorWindowTestUtility.CreateWindow();
            _createdWindows.Add(window);
            EditorWindowTestUtility.ShowWindow(window);
            window.rootVisualElement.Add(root);
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

        private static bool InvokeBuildAndRenderOverlay(MessageAwareComponent component)
        {
            Assert.That(component, Is.Not.Null, "Component under test must not be null.");

            MethodInfo method = typeof(MessageAwareComponentInspectorOverlay).GetMethod(
                "BuildAndRenderOverlay",
                BindingFlags.Static | BindingFlags.NonPublic
            );
            Assert.That(
                method,
                Is.Not.Null,
                "MessageAwareComponentInspectorOverlay.BuildAndRenderOverlay was not found. "
                    + "If this method was renamed, update this test helper."
            );

            try
            {
                object result = method.Invoke(null, new object[] { component });
                Assert.That(
                    result,
                    Is.TypeOf<bool>(),
                    "BuildAndRenderOverlay must return bool so callers can reason about whether overlay UI was emitted."
                );
                return (bool)result;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                Assert.Fail(
                    "BuildAndRenderOverlay threw unexpectedly while base-call checks were disabled. "
                        + $"Inner exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}. "
                        + $"GUI context: {DescribeCurrentGuiContext()}."
                );
                return false;
            }
        }

        private static string DescribeCurrentGuiContext()
        {
            Event currentEvent = Event.current;
            if (currentEvent == null)
            {
                return "Event.current=<null>";
            }
            return $"Event.current.type={currentEvent.type}";
        }

        private Object CreateTargetForScenario(OverlayTargetScenario scenario)
        {
            switch (scenario)
            {
                case OverlayTargetScenario.NullObject:
                    return null;
                case OverlayTargetScenario.GameObject:
                    return CreateTrackedObject("OverlayTargetGameObject");
                case OverlayTargetScenario.Transform:
                    return CreateTrackedObject("OverlayTargetTransform").transform;
                default:
                    Assert.Fail($"Unhandled {nameof(OverlayTargetScenario)} value: {scenario}.");
                    return null;
            }
        }

        private GameObject CreateTrackedObject(string name)
        {
            GameObject gameObject = new(name);
            _createdObjects.Add(gameObject);
            return gameObject;
        }
    }

    // Helper subclass used by the editor-selection / body-emission tests. Marked internal
    // because Unity cannot serialize private nested MonoBehaviours during domain reload, and
    // [AddComponentMenu("")] hides it from the inspector's Add Component picker.
    [AddComponentMenu("")]
    internal sealed class EmptyMessageAwareComponentForFallbackTest : MessageAwareComponent { }

    [AddComponentMenu("")]
    internal sealed class SerializedFieldMessageAwareComponentForFallbackTest
        : MessageAwareComponent
    {
        [SerializeField]
        private int _value;
    }

    [AddComponentMenu("")]
    internal sealed class CustomEditorMessageAwareComponentForFallbackTest
        : MessageAwareComponent { }

    [CustomEditor(typeof(CustomEditorMessageAwareComponentForFallbackTest))]
    internal sealed class SpecificMessageAwareComponentCustomEditorForFallbackTest
        : UnityEditor.Editor { }
}
#endif
