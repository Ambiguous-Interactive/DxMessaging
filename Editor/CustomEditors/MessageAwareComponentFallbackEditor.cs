namespace DxMessaging.Editor.CustomEditors
{
#if UNITY_EDITOR
    using System;
    using DxMessaging.Editor.Analyzers;
    using DxMessaging.Unity;
    using UnityEditor;
    using UnityEditor.UIElements;
    using UnityEngine.UIElements;

    /// <summary>
    /// Primary CustomEditor for every <see cref="MessageAwareComponent"/> subclass. Hosts the
    /// DxMessaging warning above a default inspector body that keeps Unity's serialized field
    /// surface intact.
    /// </summary>
    /// <remarks>
    /// We register as a non-fallback (primary) editor with
    /// <c>editorForChildClasses: true</c>. Several alternatives were tried and rejected:
    ///
    /// <list type="number">
    /// <item>
    /// <b><c>isFallback = true</c>:</b> Unity selects this editor only when no other matches.
    /// In practice that meant Unity's <c>GenericInspector</c> handled every
    /// <see cref="MessageAwareComponent"/> subclass and the warning HelpBox vanished entirely
    /// (Unity 2021's <see cref="Editor.finishedDefaultHeaderGUI"/> hook did not reliably fire
    /// for those types). This regressed the analyzer warning surface.
    /// </item>
    /// <item>
    /// <b>Manual <see cref="SerializedObject"/> iteration that skips <c>m_Script</c>:</b> the
    /// rationale was to avoid a "duplicate Script row," but Unity does NOT draw <c>m_Script</c>
    /// in the component header -- <see cref="Editor.DrawDefaultInspector"/> draws the same
    /// disabled "Script" row that <c>GenericInspector</c> draws. Skipping it produced a visible
    /// vertical gap below the header for empty subclasses, because the row Unity reserves for
    /// the script reference was left blank.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// The current primary path is <see cref="CreateInspectorGUI"/>: it hosts the UI Toolkit
    /// warning view, then fills Unity's default Inspector body through
    /// <see cref="InspectorElement.FillDefaultInspector"/> so the disabled Script row and
    /// serialized fields remain present. To avoid double-rendering when the header hook ALSO
    /// fires for our editor instance (Unity 2022+),
    /// <see cref="MessageAwareComponentInspectorOverlay"/> unconditionally skips the header path
    /// for <see cref="MessageAwareComponentFallbackEditor"/> instances.
    /// </para>
    ///
    /// <para>
    /// <see cref="OnInspectorGUI"/> remains as an IMGUI compatibility fallback. We do NOT
    /// short-circuit it on event type. Unity invokes editors twice per frame (Layout + Repaint),
    /// and both passes MUST emit identical control counts, otherwise the inspector window's layout
    /// cache is corrupted and adjacent components fail to render. See
    /// <see cref="MessageAwareComponentInspectorOverlay.RenderInsideOnInspectorGUI"/> for the
    /// matching invariant on the overlay side.
    /// </para>
    ///
    /// <para>
    /// User-defined custom editors for specific <see cref="MessageAwareComponent"/> subclasses
    /// still win precedence: a <c>[CustomEditor(typeof(MySpecificSubclass))]</c> is more
    /// specific than our <c>editorForChildClasses</c> registration, so Unity selects the user's
    /// editor for that subclass. The header-hook overlay still surfaces the warning above the
    /// user's editor in that case.
    /// </para>
    /// </remarks>
    [CustomEditor(typeof(MessageAwareComponent), true)]
    [CanEditMultipleObjects]
    public sealed class MessageAwareComponentFallbackEditor : Editor
    {
        internal const string RootName = "dxmessaging-fallback-inspector";
        internal const string RootClassName = "dxmessaging-fallback-inspector";
        internal const string WarningHostName = "dxmessaging-fallback-inspector-warning-host";
        internal const string WarningHostClassName = "dxmessaging-fallback-inspector-warning-host";
        internal const string DefaultInspectorBodyName =
            "dxmessaging-fallback-inspector-default-body";
        internal const string DefaultInspectorBodyClassName =
            "dxmessaging-fallback-inspector-default-body";

        public override VisualElement CreateInspectorGUI()
        {
            return BuildInspectorGUI(
                this,
                () => MessageAwareComponentInspectorOverlay.ResolveInspectorState(target)
            );
        }

        public override void OnInspectorGUI()
        {
            // Render the overlay BEFORE the default body so the warning appears prominently at
            // the top of the inspector. The overlay's render body has identical Layout/Repaint
            // control counts, so we can call it unconditionally here.
            MessageAwareComponentInspectorOverlay.RenderInsideOnInspectorGUI(target);

            // Match Unity's GenericInspector exactly; including the disabled "Script" row that
            // every MonoBehaviour inspector shows. This is intentional: skipping the script row
            // creates a visible empty gap below the header for subclasses with no
            // [SerializeField] fields.
            DrawDefaultInspector();
        }

        internal static VisualElement BuildInspectorGUI(
            Editor editor,
            Func<MessageAwareComponentInspectorState> resolveState
        )
        {
            if (editor == null)
            {
                throw new ArgumentNullException(nameof(editor));
            }
            if (resolveState == null)
            {
                throw new ArgumentNullException(nameof(resolveState));
            }

            VisualElement root = new() { name = RootName };
            DxMessagingEditorTheme.Apply(root);
            root.AddToClassList(RootClassName);

            VisualElement warningHost = new() { name = WarningHostName };
            warningHost.AddToClassList(WarningHostClassName);
            root.Add(warningHost);

            InspectorWarningBinding binding = new(warningHost, resolveState);
            root.userData = binding;
            binding.Refresh();

            VisualElement defaultBody = new() { name = DefaultInspectorBodyName };
            defaultBody.AddToClassList(DefaultInspectorBodyClassName);
            InspectorElement.FillDefaultInspector(defaultBody, editor.serializedObject, editor);
            root.Add(defaultBody);

            root.RegisterCallback<AttachToPanelEvent>(_ => binding.Connect());
            root.RegisterCallback<DetachFromPanelEvent>(_ => binding.Disconnect());

            return root;
        }

        internal static void RefreshInspectorWarning(VisualElement root)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }
            if (root.userData is not InspectorWarningBinding binding)
            {
                throw new InvalidOperationException(
                    "Fallback inspector root is missing its warning binding."
                );
            }

            binding.Refresh();
        }

        private sealed class InspectorWarningBinding
        {
            private readonly VisualElement _warningHost;
            private readonly Func<MessageAwareComponentInspectorState> _resolveState;
            private bool _connected;
            private bool _refreshRetryScheduled;

            internal InspectorWarningBinding(
                VisualElement warningHost,
                Func<MessageAwareComponentInspectorState> resolveState
            )
            {
                _warningHost = warningHost ?? throw new ArgumentNullException(nameof(warningHost));
                _resolveState =
                    resolveState ?? throw new ArgumentNullException(nameof(resolveState));
            }

            internal void Connect()
            {
                if (_connected)
                {
                    return;
                }

                DxMessagingConsoleHarvester.ReportUpdated += Refresh;
                MessageAwareComponentInspectorOverlay.InspectorStateInvalidated += Refresh;
                _connected = true;
                Refresh();
            }

            internal void Disconnect()
            {
                if (!_connected)
                {
                    return;
                }

                DxMessagingConsoleHarvester.ReportUpdated -= Refresh;
                MessageAwareComponentInspectorOverlay.InspectorStateInvalidated -= Refresh;
                _connected = false;
            }

            internal void Refresh()
            {
                MessageAwareComponentInspectorState state = _resolveState();
                if (state.Kind == MessageAwareComponentInspectorStateKind.None)
                {
                    if (
                        MessageAwareComponentInspectorOverlay.IsInspectorResolutionTransientlyBlocked()
                    )
                    {
                        ScheduleRefreshRetry();
                        return;
                    }

                    _warningHost.Clear();
                    _warningHost.style.display = DisplayStyle.None;
                    return;
                }

                _refreshRetryScheduled = false;
                _warningHost.Clear();
                _warningHost.style.display = DisplayStyle.Flex;
                _warningHost.Add(MessageAwareComponentInspectorOverlay.CreateInspectorView(state));
            }

            private void ScheduleRefreshRetry()
            {
                if (_refreshRetryScheduled)
                {
                    return;
                }

                _refreshRetryScheduled = true;
                System.Action<System.Action> schedule =
                    MessageAwareComponentInspectorOverlay.TransientRefreshScheduler
                    ?? DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
                schedule(() =>
                {
                    _refreshRetryScheduled = false;
                    Refresh();
                });
            }
        }
    }
#endif
}
