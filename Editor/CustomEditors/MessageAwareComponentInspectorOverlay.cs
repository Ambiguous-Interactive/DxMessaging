namespace DxMessaging.Editor.CustomEditors
{
#if UNITY_EDITOR
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Editor.Analyzers;
    using DxMessaging.Editor.Settings;
    using DxMessaging.Unity;
    using UnityEditor;
    using UnityEditorInternal;
    using UnityEngine;
    using UnityEngine.UIElements;

    /// <summary>
    /// Header-injection overlay for every Inspector showing a <see cref="MessageAwareComponent"/> subclass.
    /// </summary>
    /// <remarks>
    /// The package-owned fallback editor hosts the UI Toolkit warning view for subclasses without
    /// a more specific user editor. This header hook remains for user-defined custom editors, so
    /// they can keep their own Inspector body while still seeing DxMessaging base-call warnings.
    /// The overlay reads its data from <see cref="DxMessagingConsoleHarvester"/> (which reflects
    /// directly into Unity's <c>UnityEditor.LogEntries</c> console store) and from
    /// <see cref="DxMessagingSettings"/> (project-wide ignore list and master toggle).
    ///
    /// <para>
    /// <b>Layout/Repaint control-count invariant.</b> When the overlay renders from inside an
    /// <see cref="Editor.OnInspectorGUI"/> body (the fallback CustomEditor path), Unity invokes
    /// us TWICE per frame: once with <c>Event.current.type == EventType.Layout</c> (where every
    /// <c>EditorGUILayout.*</c> call REGISTERS a control) and once with <c>EventType.Repaint</c>
    /// (where the registered controls are drawn). The two passes MUST emit identical control
    /// counts, otherwise Unity's layout cache for the entire inspector window is corrupted and
    /// adjacent components fail to render. That is why we expose two entry points:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <see cref="DrawHeader"/> (registered to <see cref="Editor.finishedDefaultHeaderGUI"/>) is
    /// post-body and Unity has already settled layout for the inspector by the time it fires --
    /// gating on <c>EventType.Repaint</c> there is safe.
    /// </item>
    /// <item>
    /// <see cref="RenderInsideOnInspectorGUI"/> is called from inside an editor body and CANNOT
    /// gate on event type. It must call the same <c>EditorGUILayout</c> sequence on both passes.
    /// </item>
    /// </list>
    /// </remarks>
    [InitializeOnLoad]
    public static class MessageAwareComponentInspectorOverlay
    {
        // Per-Repaint latch keyed on instanceID for the header-hook entry point. We render once
        // per Repaint event per target. EventType.Layout marks the start of a fresh GUI cycle, so
        // we clear the set then; rendering happens on EventType.Repaint, which Unity guarantees
        // fires once per visible inspector per frame.
        //
        // NOTE: cross-path dedupe between the header hook and the OnInspectorGUI hook is
        // accomplished by an UNCONDITIONAL skip at the top of <see cref="DrawHeader"/> when the
        // target editor is our fallback CustomEditor; see that method's comment. We do NOT use
        // a per-frame "header drew" set, because such a set would necessarily be populated only
        // on the Repaint pass of the header hook, while OnInspectorGUI runs on BOTH the Layout
        // and Repaint passes; that asymmetry would corrupt the inspector's layout cache.
        private static readonly HashSet<int> _renderedThisRepaint = new();

        internal static System.Action<System.Action> AssetDatabaseMutationScheduler { get; set; } =
            DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;

        internal static System.Action<System.Action> TransientRefreshScheduler { get; set; } =
            DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;

        internal static System.Action InspectorRepaintAction { get; set; } = RepaintAllInspectors;

        internal static System.Func<bool> InspectorResolutionTransientBlocker { get; set; } =
            () => EditorApplication.isCompiling || EditorApplication.isUpdating;

        internal static System.Func<MessageAwareComponent, bool> ImGuiRenderAction { get; set; } =
            BuildAndRenderOverlay;

        internal static System.Action<
            MessageAwareComponent,
            BaseCallReportEntry
        > OpenScriptAction { get; set; } = OpenScriptForComponentCore;

        internal static event System.Action InspectorStateInvalidated;

        static MessageAwareComponentInspectorOverlay()
        {
            Editor.finishedDefaultHeaderGUI += DrawHeader;
            DxMessagingConsoleHarvester.ReportUpdated += RepaintAllInspectors;
        }

        private static void RepaintAllInspectors()
        {
            try
            {
                // InternalEditorUtility.RepaintAllViews is the cheap path: it walks the
                // existing GUIView list once. Resources.FindObjectsOfTypeAll<Editor>() allocates
                // a fresh array of every Editor instance Unity has loaded, which is wasteful
                // when we just want a redraw signal.
                InternalEditorUtility.RepaintAllViews();
            }
            catch (System.Exception ex)
            {
                DxMessagingEditorLog.LogWarning(
                    "Failed to repaint inspectors after analyzer report update.",
                    ex
                );
            }
        }

        private static void DrawHeader(Editor editor)
        {
            TryRenderHeaderHook(editor, Event.current != null ? Event.current.type : null);
        }

        internal static bool TryRenderHeaderHook(Editor editor, EventType? eventType)
        {
            if (!ShouldRenderHeaderHook(editor))
            {
                return false;
            }

            return TryRenderForHeaderHook(editor.target, eventType);
        }

        internal static bool ShouldRenderHeaderHook(Editor editor)
        {
            if (editor == null)
            {
                return false;
            }

            // If our own fallback CustomEditor is the editor instance, skip the header path
            // entirely; CreateInspectorGUI or OnInspectorGUI owns package fallback rendering and
            // we would otherwise render twice. Unconditional skip (not gated on EventType) keeps
            // control counts balanced on both Layout and Repaint passes.
            return editor is not MessageAwareComponentFallbackEditor;
        }

        /// <summary>
        /// Header-hook render body. Fires after Unity's default header has been drawn, so the
        /// inspector's layout pass for this editor has already completed. Safe to gate on
        /// <see cref="EventType.Repaint"/> here -- we are not inside an OnInspectorGUI body.
        /// </summary>
        private static bool TryRenderForHeaderHook(Object target, EventType? eventType)
        {
            if (target == null)
            {
                return false;
            }
            if (target is not MessageAwareComponent messageAwareComponent)
            {
                return false;
            }
            if (!eventType.HasValue)
            {
                return false;
            }
            if (eventType.Value == EventType.Layout)
            {
                // Start of a fresh GUI cycle; wipe the per-Repaint latch.
                _renderedThisRepaint.Clear();
                return false;
            }
            if (eventType.Value != EventType.Repaint)
            {
                return false;
            }
            int instanceId = DxMessaging.Core.InstanceId.StableId(messageAwareComponent);
            if (!_renderedThisRepaint.Add(instanceId))
            {
                return false;
            }

            System.Func<MessageAwareComponent, bool> render =
                ImGuiRenderAction ?? BuildAndRenderOverlay;
            return render.Invoke(messageAwareComponent);
        }

        /// <summary>
        /// OnInspectorGUI entry point. Called from inside the fallback CustomEditor's
        /// <see cref="Editor.OnInspectorGUI"/>, where Unity invokes the editor on BOTH the Layout
        /// pass and the Repaint pass. This method MUST emit the same <c>EditorGUILayout</c> calls
        /// on both passes, so it does NOT gate on <see cref="EventType"/> and does NOT latch.
        /// Cross-path dedupe with the header-hook path is handled inside
        /// <see cref="DrawHeader"/>, which unconditionally skips when the editor is our fallback.
        /// </summary>
        internal static void RenderInsideOnInspectorGUI(Object target)
        {
            if (target is not MessageAwareComponent messageAwareComponent)
            {
                return;
            }
            BuildAndRenderOverlay(messageAwareComponent);
        }

        /// <summary>
        /// Rendering body shared by both entry points. Performs ALL gating decisions up-front
        /// before any <c>EditorGUILayout.*</c> call, then runs straight-line layout calls. This
        /// guarantees the function emits an identical sequence of layout calls on the Layout and
        /// Repaint passes when invoked from within <see cref="Editor.OnInspectorGUI"/>.
        /// </summary>
        /// <returns>True if the HelpBox + buttons were drawn; false if we drew nothing.</returns>
        private static bool BuildAndRenderOverlay(MessageAwareComponent messageAwareComponent)
        {
            MessageAwareComponentInspectorState state = ResolveInspectorState(
                messageAwareComponent
            );

            if (state.Kind == MessageAwareComponentInspectorStateKind.None)
            {
                return false;
            }

            // ---- Render phase: straight-line EditorGUILayout calls, identical sequence on
            // every pass. Wrapped in a vertical group so any internal mismatch we missed cannot
            // propagate to sibling inspectors. ----
            EditorGUILayout.BeginVertical();
            try
            {
                switch (state.Kind)
                {
                    case MessageAwareComponentInspectorStateKind.HarvesterUnavailable:
                        EditorGUILayout.HelpBox(
                            "DxMessaging inspector overlay is disabled on this Unity version. "
                                + "Check the console for DXMSG006/007/009 warnings instead.",
                            MessageType.Info
                        );
                        break;
                    case MessageAwareComponentInspectorStateKind.IgnoredType:
                        DrawIgnoredBox(state.Component, state.Settings, state.FullName);
                        break;
                    case MessageAwareComponentInspectorStateKind.MissingBaseCallWarning:
                        DrawWarningBox(
                            state.Component,
                            state.Settings,
                            state.FullName,
                            state.Entry,
                            state.IsFreshThisSession
                        );
                        break;
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }

            return true;
        }

        internal static VisualElement CreateInspectorView(Object target)
        {
            return CreateInspectorView(ResolveInspectorState(target));
        }

        internal static VisualElement CreateInspectorView(MessageAwareComponentInspectorState state)
        {
            return MessageAwareComponentInspectorView.Create(state, CreateInspectorViewActions());
        }

        internal static void ResetTestSeams()
        {
            _renderedThisRepaint.Clear();
            AssetDatabaseMutationScheduler = DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
            TransientRefreshScheduler = DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
            InspectorRepaintAction = RepaintAllInspectors;
            InspectorResolutionTransientBlocker = () =>
                EditorApplication.isCompiling || EditorApplication.isUpdating;
            ImGuiRenderAction = BuildAndRenderOverlay;
            OpenScriptAction = OpenScriptForComponentCore;
        }

        internal static bool IsInspectorResolutionTransientlyBlocked()
        {
            System.Func<bool> isBlocked =
                InspectorResolutionTransientBlocker
                ?? (() => EditorApplication.isCompiling || EditorApplication.isUpdating);
            return isBlocked.Invoke();
        }

        internal static MessageAwareComponentInspectorState ResolveInspectorState(Object target)
        {
            if (target == null)
            {
                return MessageAwareComponentInspectorState.None;
            }

            // Mid-compile / mid-import is the worst time to dereference the settings asset:
            // AssetDatabase may be in a transitional state. Bail and let the next OnGUI redraw
            // pick up where we left off.
            if (IsInspectorResolutionTransientlyBlocked())
            {
                return MessageAwareComponentInspectorState.None;
            }

            if (target is not MessageAwareComponent messageAwareComponent)
            {
                return MessageAwareComponentInspectorState.None;
            }

            DxMessagingSettings settings;
            try
            {
                settings = DxMessagingSettings.GetOrCreateSettings();
            }
            catch (System.Exception ex)
            {
                DxMessagingEditorLog.LogWarning("Inspector overlay could not load settings.", ex);
                return MessageAwareComponentInspectorState.None;
            }

            string fullName = GetOverlayTypeName(messageAwareComponent);
            BaseCallReportEntry entry = null;
            if (!string.IsNullOrEmpty(fullName) && DxMessagingConsoleHarvester.IsAvailable)
            {
                DxMessagingConsoleHarvester.TryGetEntry(fullName, out entry);
            }

            return ResolveInspectorState(
                messageAwareComponent,
                settings,
                DxMessagingConsoleHarvester.IsAvailable,
                DxMessagingConsoleHarvester.IsFreshThisSession,
                entry
            );
        }

        internal static MessageAwareComponentInspectorState ResolveInspectorState(
            Object target,
            DxMessagingSettings settings,
            bool harvesterAvailable,
            bool isFreshThisSession,
            BaseCallReportEntry entry
        )
        {
            if (target == null)
            {
                return MessageAwareComponentInspectorState.None;
            }

            if (target is not MessageAwareComponent messageAwareComponent)
            {
                return MessageAwareComponentInspectorState.None;
            }
            if (settings == null || !settings._baseCallCheckEnabled)
            {
                return MessageAwareComponentInspectorState.None;
            }

            // S6: System.Type.FullName renders nested types as `Outer+Nested`, but the analyzer's
            // `containingType.ToDisplayString()` (which produces the FQN we key the snapshot by)
            // renders them as `Outer.Nested`. Without this normalization the lookup misses for
            // every nested MessageAwareComponent subclass and the HelpBox never shows.
            string fullName = GetOverlayTypeName(messageAwareComponent);
            if (string.IsNullOrEmpty(fullName))
            {
                return MessageAwareComponentInspectorState.None;
            }

            if (!harvesterAvailable)
            {
                return MessageAwareComponentInspectorState.ForHarvesterUnavailable(
                    messageAwareComponent,
                    settings,
                    fullName,
                    isFreshThisSession
                );
            }

            bool isIgnored =
                settings._baseCallIgnoredTypes != null
                && settings._baseCallIgnoredTypes.Any(e =>
                    string.Equals(e, fullName, System.StringComparison.Ordinal)
                );
            if (isIgnored)
            {
                return MessageAwareComponentInspectorState.ForIgnoredType(
                    messageAwareComponent,
                    settings,
                    fullName,
                    isFreshThisSession
                );
            }

            if (entry != null && entry.missingBaseFor != null && entry.missingBaseFor.Count > 0)
            {
                return MessageAwareComponentInspectorState.ForMissingBaseCallWarning(
                    messageAwareComponent,
                    settings,
                    fullName,
                    entry,
                    isFreshThisSession
                );
            }

            // "Render nothing" branch: emit ZERO EditorGUILayout calls. This must hold on
            // both Layout and Repaint passes when called from OnInspectorGUI, so Unity's
            // layout cache stays consistent.
            return MessageAwareComponentInspectorState.None;
        }

        private static MessageAwareComponentInspectorViewActions CreateInspectorViewActions()
        {
            return new MessageAwareComponentInspectorViewActions(
                onOpenScript: state => OpenScriptForComponent(state.Component, state.Entry),
                onIgnoreType: state => TryAddIgnoredType(state.Settings, state.FullName),
                onStopIgnoring: state => TryRemoveIgnoredType(state.Settings, state.FullName)
            );
        }

        private static void NotifyInspectorStateInvalidated()
        {
            System.Action repaint = InspectorRepaintAction ?? RepaintAllInspectors;
            try
            {
                repaint.Invoke();
            }
            catch (System.Exception ex)
            {
                DxMessagingEditorLog.LogWarning(
                    "Failed to repaint inspectors after state change.",
                    ex
                );
            }

            System.Action invalidated = InspectorStateInvalidated;
            if (invalidated == null)
            {
                return;
            }

            foreach (System.Delegate callback in invalidated.GetInvocationList())
            {
                try
                {
                    ((System.Action)callback).Invoke();
                }
                catch (System.Exception ex)
                {
                    DxMessagingEditorLog.LogWarning(
                        "Failed to refresh MessageAwareComponent inspectors after state change.",
                        ex
                    );
                }
            }
        }

        private static string GetOverlayTypeName(MessageAwareComponent messageAwareComponent)
        {
            System.Type targetType = messageAwareComponent.GetType();
            return (targetType.FullName ?? string.Empty).Replace('+', '.');
        }

        private static void DrawWarningBox(
            MessageAwareComponent component,
            DxMessagingSettings settings,
            string fullName,
            BaseCallReportEntry entry,
            bool isFreshThisSession
        )
        {
            string missingMethods = string.Join(", ", entry.missingBaseFor);
            // Per-method consequence lines mirror the analyzer's DXMSG006 message text. Reading
            // the dictionary on BaseCallTypeScannerCore keeps the overlay copy in lockstep with
            // the analyzer; both are updated together when a new guarded method is added.
            System.Text.StringBuilder consequenceBuilder = new();
            foreach (string missingMethod in entry.missingBaseFor)
            {
                consequenceBuilder.Append("\n- ");
                consequenceBuilder.Append(
                    BaseCallTypeScannerCore.GetMissingBaseConsequenceLine(missingMethod, fullName)
                );
            }
            string consequenceLines = consequenceBuilder.ToString();

            // Cached-vs-fresh suffix is appended to the SAME HelpBox string rather than emitted
            // as a sibling control, which keeps the Layout and Repaint passes emitting an
            // identical sequence of EditorGUILayout.* calls regardless of harvester freshness.
            // The suffix only appears when the harvester is showing entries loaded eagerly from
            // `Library/DxMessaging/baseCallReport.json` and the first post-reload scan has not
            // yet completed; once the scan flips IsFreshThisSession to true and RepaintAllInspectors
            // fires, the overlay redraws without the suffix.
            string freshnessSuffix = isFreshThisSession
                ? string.Empty
                : "\n(cached from previous session; refreshing...)";
            string message =
                $"{fullName} has lifecycle methods that don't chain to MessageAwareComponent ({missingMethods}); DxMessaging will not function on this component."
                + consequenceLines
                + "\nSee docs/reference/analyzers.md."
                + freshnessSuffix;

            EditorGUILayout.HelpBox(message, MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Script"))
                {
                    OpenScriptForComponent(component, entry);
                }
                if (GUILayout.Button("Ignore this type"))
                {
                    TryAddIgnoredType(settings, fullName);
                }
            }
        }

        private static void DrawIgnoredBox(
            MessageAwareComponent component,
            DxMessagingSettings settings,
            string fullName
        )
        {
            EditorGUILayout.HelpBox(
                $"{fullName} is excluded from the DxMessaging base-call check.",
                MessageType.Info
            );
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Stop ignoring"))
                {
                    TryRemoveIgnoredType(settings, fullName);
                }
            }
        }

        private static void OpenScriptForComponent(
            MessageAwareComponent component,
            BaseCallReportEntry entry
        )
        {
            System.Action<MessageAwareComponent, BaseCallReportEntry> openScript =
                OpenScriptAction ?? OpenScriptForComponentCore;
            openScript.Invoke(component, entry);
        }

        private static void OpenScriptForComponentCore(
            MessageAwareComponent component,
            BaseCallReportEntry entry
        )
        {
            try
            {
                MonoScript monoScript = MonoScript.FromMonoBehaviour(component);
                if (monoScript == null)
                {
                    return;
                }
                if (entry != null && entry.line > 0)
                {
                    AssetDatabase.OpenAsset(monoScript, entry.line);
                }
                else
                {
                    AssetDatabase.OpenAsset(monoScript);
                }
            }
            catch (System.Exception ex)
            {
                DxMessagingEditorLog.LogWarning("Failed to open script.", ex);
            }
        }

        private static void TryAddIgnoredType(DxMessagingSettings settings, string fullName)
        {
            // Defer the mutation to AFTER the current frame's Layout/Repaint pair completes.
            // Mutating settings._baseCallIgnoredTypes synchronously inside a button handler
            // would flip the overlay's shape between Layout and Repaint passes of the SAME
            // frame, corrupting Unity's per-window layout cache. delayCall fires AFTER the
            // current GUI cycle, so the next frame's Layout pass sees the new state and
            // both passes emit consistent control counts.
            System.Action<System.Action> schedule =
                AssetDatabaseMutationScheduler
                ?? DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
            schedule(() =>
            {
                if (settings == null)
                {
                    return;
                }
                try
                {
                    settings.AddIgnoredType(fullName);
                    NotifyInspectorStateInvalidated();
                }
                catch (System.Exception ex)
                {
                    DxMessagingEditorLog.LogWarning(
                        $"Failed to add ignored type '{fullName}'.",
                        ex
                    );
                }
            });
        }

        private static void TryRemoveIgnoredType(DxMessagingSettings settings, string fullName)
        {
            // Same reasoning as TryAddIgnoredType: defer mutation past the current GUI cycle so
            // the overlay's shape gating remains identical on Layout and Repaint passes of THIS
            // frame. The next frame's Layout pass observes the new state; both passes agree.
            System.Action<System.Action> schedule =
                AssetDatabaseMutationScheduler
                ?? DxMessagingEditorIdle.ScheduleAssetDatabaseMutation;
            schedule(() =>
            {
                if (settings == null)
                {
                    return;
                }
                try
                {
                    settings.RemoveIgnoredType(fullName);
                    NotifyInspectorStateInvalidated();
                }
                catch (System.Exception ex)
                {
                    DxMessagingEditorLog.LogWarning(
                        $"Failed to remove ignored type '{fullName}'.",
                        ex
                    );
                }
            });
        }
    }

    internal enum MessageAwareComponentInspectorStateKind
    {
        None,
        HarvesterUnavailable,
        IgnoredType,
        MissingBaseCallWarning,
    }

    internal readonly struct MessageAwareComponentInspectorState
    {
        private MessageAwareComponentInspectorState(
            MessageAwareComponentInspectorStateKind kind,
            MessageAwareComponent component,
            DxMessagingSettings settings,
            string fullName,
            BaseCallReportEntry entry,
            bool isFreshThisSession
        )
        {
            Kind = kind;
            Component = component;
            Settings = settings;
            FullName = fullName ?? string.Empty;
            Entry = entry;
            IsFreshThisSession = isFreshThisSession;
        }

        internal static MessageAwareComponentInspectorState None { get; } =
            new(
                MessageAwareComponentInspectorStateKind.None,
                null,
                null,
                string.Empty,
                null,
                isFreshThisSession: true
            );

        internal MessageAwareComponentInspectorStateKind Kind { get; }

        internal MessageAwareComponent Component { get; }

        internal DxMessagingSettings Settings { get; }

        internal string FullName { get; }

        internal BaseCallReportEntry Entry { get; }

        internal bool IsFreshThisSession { get; }

        internal static MessageAwareComponentInspectorState ForHarvesterUnavailable(
            MessageAwareComponent component,
            DxMessagingSettings settings,
            string fullName,
            bool isFreshThisSession
        )
        {
            return new MessageAwareComponentInspectorState(
                MessageAwareComponentInspectorStateKind.HarvesterUnavailable,
                component,
                settings,
                fullName,
                null,
                isFreshThisSession
            );
        }

        internal static MessageAwareComponentInspectorState ForIgnoredType(
            MessageAwareComponent component,
            DxMessagingSettings settings,
            string fullName,
            bool isFreshThisSession
        )
        {
            return new MessageAwareComponentInspectorState(
                MessageAwareComponentInspectorStateKind.IgnoredType,
                component,
                settings,
                fullName,
                null,
                isFreshThisSession
            );
        }

        internal static MessageAwareComponentInspectorState ForMissingBaseCallWarning(
            MessageAwareComponent component,
            DxMessagingSettings settings,
            string fullName,
            BaseCallReportEntry entry,
            bool isFreshThisSession
        )
        {
            return new MessageAwareComponentInspectorState(
                MessageAwareComponentInspectorStateKind.MissingBaseCallWarning,
                component,
                settings,
                fullName,
                entry,
                isFreshThisSession
            );
        }
    }
#endif
}
