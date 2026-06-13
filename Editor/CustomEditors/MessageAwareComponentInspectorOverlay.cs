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

    /// <summary>
    /// Header-injection overlay for every Inspector showing a <see cref="MessageAwareComponent"/> subclass.
    /// </summary>
    /// <remarks>
    /// We hook <see cref="Editor.finishedDefaultHeaderGUI"/> rather than registering a
    /// <c>[CustomEditor(typeof(MessageAwareComponent), editorForChildClasses: true)]</c> so we never
    /// clobber a user's own custom editor. The overlay reads its data from
    /// <see cref="DxMessagingConsoleHarvester"/> (which reflects directly into Unity's
    /// <c>UnityEditor.LogEntries</c> console store) and from <see cref="DxMessagingSettings"/>
    /// (project-wide ignore list and master toggle).
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
            if (editor == null)
            {
                return;
            }
            // If our own fallback CustomEditor is the editor instance, skip the header path
            // entirely; the editor's OnInspectorGUI will call RenderInsideOnInspectorGUI and we
            // would otherwise render twice. Unconditional skip (not gated on EventType) keeps
            // control counts balanced on both Layout and Repaint passes.
            if (editor is MessageAwareComponentFallbackEditor)
            {
                return;
            }
            RenderForHeaderHook(editor.target);
        }

        /// <summary>
        /// Header-hook entry point. Fires after Unity's default header has been drawn, so the
        /// inspector's layout pass for this editor has already completed. Safe to gate on
        /// <see cref="EventType.Repaint"/> here -- we are not inside an OnInspectorGUI body.
        /// </summary>
        private static void RenderForHeaderHook(Object target)
        {
            if (target == null)
            {
                return;
            }
            if (target is not MessageAwareComponent messageAwareComponent)
            {
                return;
            }

            Event currentEvent = Event.current;
            if (currentEvent == null)
            {
                return;
            }
            if (currentEvent.type == EventType.Layout)
            {
                // Start of a fresh GUI cycle; wipe the per-Repaint latch.
                _renderedThisRepaint.Clear();
                return;
            }
            if (currentEvent.type != EventType.Repaint)
            {
                return;
            }
            int instanceId = messageAwareComponent.GetInstanceID();
            if (!_renderedThisRepaint.Add(instanceId))
            {
                return;
            }

            BuildAndRenderOverlay(messageAwareComponent);
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
            // ---- Gating phase: every "should we draw?" decision happens here, before any
            // EditorGUILayout call. The result is a single bool: shouldRender. ----

            // Mid-compile / mid-import is the worst time to dereference the settings asset:
            // AssetDatabase may be in a transitional state. Bail and let the next OnGUI redraw
            // pick up where we left off.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return false;
            }

            DxMessagingSettings settings;
            try
            {
                settings = DxMessagingSettings.GetOrCreateSettings();
            }
            catch (System.Exception ex)
            {
                DxMessagingEditorLog.LogWarning("Inspector overlay could not load settings.", ex);
                return false;
            }

            if (settings == null || !settings._baseCallCheckEnabled)
            {
                return false;
            }

            // S6: System.Type.FullName renders nested types as `Outer+Nested`, but the analyzer's
            // `containingType.ToDisplayString()` (which produces the FQN we key the snapshot by)
            // renders them as `Outer.Nested`. Without this normalization the lookup misses for
            // every nested MessageAwareComponent subclass and the HelpBox never shows.
            System.Type targetType = messageAwareComponent.GetType();
            string fullName = (targetType.FullName ?? string.Empty).Replace('+', '.');
            if (string.IsNullOrEmpty(fullName))
            {
                return false;
            }

            // Decide which of the three render shapes (if any) to draw.
            // 0 = render nothing; 1 = harvester-unavailable info; 2 = ignored-type info; 3 = warning.
            int shape = 0;
            BaseCallReportEntry entry = null;
            bool isIgnored = false;

            if (!DxMessagingConsoleHarvester.IsAvailable)
            {
                shape = 1;
            }
            else
            {
                isIgnored =
                    settings._baseCallIgnoredTypes != null
                    && settings._baseCallIgnoredTypes.Any(e =>
                        string.Equals(e, fullName, System.StringComparison.Ordinal)
                    );
                if (isIgnored)
                {
                    shape = 2;
                }
                else if (
                    DxMessagingConsoleHarvester.TryGetEntry(fullName, out entry)
                    && entry != null
                    && entry.missingBaseFor != null
                    && entry.missingBaseFor.Count > 0
                )
                {
                    shape = 3;
                }
            }

            if (shape == 0)
            {
                // "Render nothing" branch: emit ZERO EditorGUILayout calls. This must hold on
                // both Layout and Repaint passes when called from OnInspectorGUI, so Unity's
                // layout cache stays consistent.
                return false;
            }

            // ---- Render phase: straight-line EditorGUILayout calls, identical sequence on
            // every pass. Wrapped in a vertical group so any internal mismatch we missed cannot
            // propagate to sibling inspectors. ----
            EditorGUILayout.BeginVertical();
            try
            {
                switch (shape)
                {
                    case 1:
                        EditorGUILayout.HelpBox(
                            "DxMessaging inspector overlay is disabled on this Unity version. "
                                + "Check the console for DXMSG006/007/009 warnings instead.",
                            MessageType.Info
                        );
                        break;
                    case 2:
                        DrawIgnoredBox(messageAwareComponent, settings, fullName);
                        break;
                    case 3:
                        DrawWarningBox(messageAwareComponent, settings, fullName, entry);
                        break;
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }

            return true;
        }

        private static void DrawWarningBox(
            MessageAwareComponent component,
            DxMessagingSettings settings,
            string fullName,
            BaseCallReportEntry entry
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
            string freshnessSuffix = DxMessagingConsoleHarvester.IsFreshThisSession
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
            DxMessagingEditorIdle.ScheduleAssetDatabaseMutation(() =>
            {
                if (settings == null)
                {
                    return;
                }
                try
                {
                    settings.AddIgnoredType(fullName);
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
            DxMessagingEditorIdle.ScheduleAssetDatabaseMutation(() =>
            {
                if (settings == null)
                {
                    return;
                }
                try
                {
                    settings.RemoveIgnoredType(fullName);
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
#endif
}
