namespace DxMessaging.Editor.CustomEditors
{
#if UNITY_EDITOR
    using System.Collections.Generic;
    using System.Linq;
    using Core;
    using Core.Diagnostics;
    using Core.MessageBus;
    using Core.Messages;
    using DxMessaging.Editor.Testing;
    using DxMessaging.Unity;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    [CustomEditor(typeof(MessagingComponent))]
    public sealed class MessagingComponentEditor : Editor
    {
        private const int PageSize = 5;

        private readonly Dictionary<MonoBehaviour, bool> _listenerFoldouts = new();
        private readonly Dictionary<MonoBehaviour, int> _listenerRegistrationPaging = new();
        private readonly Dictionary<MonoBehaviour, bool> _listenerBufferFoldouts = new();
        private readonly Dictionary<MonoBehaviour, int> _listenerBufferPaging = new();

        private bool _globalBufferExpanded;
        private int _globalBufferPaging;

        private GUIStyle _matchingStyle;
        private GUIStyle _potentialMatchStyle;
        private GUIStyle _defaultStyle;
        private GUIStyle _leftAlignedStyle;
        private GUIStyle _rightAlignedStyle;

        /// <summary>
        /// Which skin the two colored styles were built for. Switching the editor theme repaints
        /// without reloading the domain, so a cached style has to be rebuilt or it keeps painting the
        /// other skin's color.
        /// </summary>
        private bool _coloredStylesUseProSkin;

        private void OnEnable()
        {
            _listenerFoldouts.Clear();
        }

        /// <summary>
        /// Draws the custom inspector, including diagnostics summaries and listener management controls.
        /// </summary>
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            // This inspector is the package's remaining IMGUI surface, so it cannot pick colors up
            // from the stylesheet the way the UI Toolkit windows do. It reads them from the palette
            // instead: `Color.green` and `Color.yellow` are outside the design system, and both are
            // close to unreadable as label text on the light skin.
            bool proSkin = EditorGUIUtility.isProSkin;
            if (_matchingStyle == null || _coloredStylesUseProSkin != proSkin)
            {
                _coloredStylesUseProSkin = proSkin;
                _matchingStyle = new GUIStyle(EditorStyles.label)
                {
                    normal =
                    {
                        textColor = DxMessagingEditorPalette.ForSkin(
                            DxMessagingEditorPalette.BroadcastText,
                            DxMessagingEditorPalette.BroadcastTextOnLight,
                            proSkin
                        ),
                    },
                    fontStyle = FontStyle.Bold,
                };
                _potentialMatchStyle = new GUIStyle(EditorStyles.label)
                {
                    normal =
                    {
                        textColor = DxMessagingEditorPalette.ForSkin(
                            DxMessagingEditorPalette.Amber,
                            DxMessagingEditorPalette.AmberOnLight,
                            proSkin
                        ),
                    },
                    fontStyle = FontStyle.Bold,
                };
            }
            _defaultStyle ??= new GUIStyle(EditorStyles.label);
            _leftAlignedStyle ??= new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
            };
            _rightAlignedStyle ??= new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleRight,
            };

            MessagingComponent component = target as MessagingComponent;
            if (component == null)
            {
                return;
            }

            MessagingComponentInspectorState inspectorState =
                MessagingComponentEditorHarness.Capture(component);
            ProviderDiagnosticsView providerDiagnostics = inspectorState.ProviderDiagnostics;

            if (providerDiagnostics.SerializedProviderMissingWarning)
            {
                EditorGUILayout.HelpBox(
                    "Auto-configure serialized provider is enabled, but no provider asset is assigned.",
                    MessageType.Warning
                );
            }

            if (providerDiagnostics.SerializedProviderNullBusWarning)
            {
                EditorGUILayout.HelpBox(
                    "The serialized provider resolved without returning a message bus. Verify the provider implementation.",
                    MessageType.Warning
                );
            }

            if (component._registeredListeners.Count == 0)
            {
                EditorGUILayout.LabelField("No listeners registered.");
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Global Diagnostics", EditorStyles.boldLabel);

            using (new GUILayout.HorizontalScope())
            {
                if (
                    component._registeredListeners.Values.Any(token => !token.DiagnosticMode)
                    && GUILayout.Button(
                        $"Enable Diagnostics for All ({component._registeredListeners.Count})"
                    )
                )
                {
                    foreach (
                        MessageRegistrationToken token in component._registeredListeners.Values
                    )
                    {
                        token.DiagnosticMode = true;
                    }
                }

                if (
                    component._registeredListeners.Values.Any(token => token.DiagnosticMode)
                    && GUILayout.Button(
                        $"Disable Diagnostics for All ({component._registeredListeners.Count})"
                    )
                )
                {
                    foreach (
                        MessageRegistrationToken token in component._registeredListeners.Values
                    )
                    {
                        token.DiagnosticMode = false;
                    }
                }
            }

            IMessageBus globalBus = MessageHandler.MessageBus;
            if (globalBus is not MessageBus concreteBus)
            {
                EditorGUILayout.HelpBox(
                    "Global diagnostics controls are unavailable because the active global bus is not the default DxMessaging MessageBus implementation.",
                    MessageType.Info
                );
            }
            else if (!concreteBus.DiagnosticsMode)
            {
                if (GUILayout.Button("Enable Global Diagnostics"))
                {
                    concreteBus.DiagnosticsMode = true;
                }
            }
            else
            {
                if (GUILayout.Button("Disable Global Diagnostics"))
                {
                    concreteBus.DiagnosticsMode = false;
                }
                else
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Global Buffer", EditorStyles.boldLabel);

                    _globalBufferExpanded = EditorGUILayout.Foldout(
                        _globalBufferExpanded,
                        "Global Messages",
                        true
                    );
                    int totalGlobalMessages = concreteBus._emissionBuffer.Count;
                    if (_globalBufferExpanded && totalGlobalMessages > 0)
                    {
                        int page = _globalBufferPaging;
                        int totalPages = (totalGlobalMessages + PageSize - 1) / PageSize;
                        page = Mathf.Clamp(page, 0, totalPages - 1);
                        EditorGUI.indentLevel++;
                        if (totalPages > 1)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUI.enabled = page > 0;
                                if (GUILayout.Button("<< Previous"))
                                {
                                    _globalBufferPaging--;
                                }

                                GUI.enabled = true;

                                GUILayout.FlexibleSpace();
                                EditorGUILayout.LabelField($"Page {page + 1} of {totalPages}");
                                GUILayout.FlexibleSpace();

                                GUI.enabled = page < totalPages - 1;
                                if (GUILayout.Button("Next >>"))
                                {
                                    _globalBufferPaging++;
                                }

                                GUI.enabled = true;
                            }
                        }

                        MessageEmissionData[] pagedGlobalMessages = concreteBus
                            ._emissionBuffer.Reverse()
                            .Skip(page * PageSize)
                            .Take(PageSize)
                            .ToArray();
                        foreach (MessageEmissionData globalEmissionData in pagedGlobalMessages)
                        {
                            using (new EditorGUILayout.VerticalScope("box"))
                            {
                                GUIStyle style;
                                InstanceId? context = globalEmissionData.context;
                                if (context?.Object != null)
                                {
                                    Object unityObject = context.Value.Object;
                                    if (
                                        (
                                            typeof(ITargetedMessage).IsAssignableFrom(
                                                globalEmissionData.message.MessageType
                                            )
                                            && (
                                                unityObject == component.gameObject
                                                || component
                                                    .GetComponents<MonoBehaviour>()
                                                    .Any(script => script == unityObject)
                                            )
                                        )
                                        || (
                                            typeof(IBroadcastMessage).IsAssignableFrom(
                                                globalEmissionData.message.MessageType
                                            )
                                            && (
                                                component._registeredListeners.Keys.Any(script =>
                                                    script.gameObject == unityObject
                                                    || script
                                                        .GetComponents<MonoBehaviour>()
                                                        .Any(matchedScript =>
                                                            matchedScript == unityObject
                                                        )
                                                )
                                            )
                                        )
                                    )
                                    {
                                        style = component._registeredListeners.Values.Any(
                                            listener =>
                                                listener._emissionBuffer.Contains(
                                                    globalEmissionData
                                                )
                                        )
                                            ? _matchingStyle
                                            : _potentialMatchStyle;
                                    }
                                    else
                                    {
                                        style = _defaultStyle;
                                    }
                                }
                                else
                                {
                                    style = component._registeredListeners.Values.Any(listener =>
                                        listener._emissionBuffer.Contains(globalEmissionData)
                                    )
                                        ? _matchingStyle
                                        : _defaultStyle;
                                }

                                GUIContent labelContent = new("Message Type");
                                GUIContent valueContent = new(
                                    globalEmissionData.message.MessageType.Name,
                                    globalEmissionData.stackTrace
                                );

                                EditorGUILayout.LabelField(labelContent, valueContent, style);
                                if (context?.Object != null)
                                {
                                    Object unityObject = context.Value.Object;
                                    string label = "Context";
                                    if (
                                        typeof(ITargetedMessage).IsAssignableFrom(
                                            globalEmissionData.message.MessageType
                                        )
                                    )
                                    {
                                        label = "Target";
                                    }
                                    else if (
                                        typeof(IBroadcastMessage).IsAssignableFrom(
                                            globalEmissionData.message.MessageType
                                        )
                                    )
                                    {
                                        label = "Source";
                                    }
                                    EditorGUILayout.ObjectField(
                                        label,
                                        unityObject,
                                        typeof(Object),
                                        true
                                    );
                                }
                            }
                        }
                    }
                }
            }
            List<MonoBehaviour> listeners = new(component._registeredListeners.Keys);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Local Buffer", EditorStyles.boldLabel);
            foreach (MonoBehaviour listener in listeners)
            {
                if (!component._registeredListeners[listener].DiagnosticMode)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(
                        $"Diagnostics are disabled for {listener.GetType().Name}. Enable diagnostics to view diagnostics data.",
                        MessageType.Info
                    );
                }
                else
                {
                    _listenerBufferFoldouts.TryAdd(listener, false);
                    _listenerBufferFoldouts[listener] = EditorGUILayout.Foldout(
                        _listenerBufferFoldouts[listener],
                        listener.GetType().Name,
                        true
                    );

                    int totalMessages = component
                        ._registeredListeners[listener]
                        ._emissionBuffer
                        .Count;
                    if (_listenerBufferFoldouts[listener] && totalMessages > 0)
                    {
                        int page = _listenerBufferPaging.GetValueOrDefault(listener, 0);
                        int totalPages = (totalMessages + PageSize - 1) / PageSize;
                        page = Mathf.Clamp(page, 0, totalPages - 1);
                        _listenerBufferPaging[listener] = page;
                        EditorGUI.indentLevel++;
                        if (totalPages > 1)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUI.enabled = page > 0;
                                if (GUILayout.Button("<< Previous"))
                                {
                                    _listenerBufferPaging[listener]--;
                                }

                                GUI.enabled = true;

                                GUILayout.FlexibleSpace();
                                EditorGUILayout.LabelField($"Page {page + 1} of {totalPages}");
                                GUILayout.FlexibleSpace();

                                GUI.enabled = page < totalPages - 1;
                                if (GUILayout.Button("Next >>"))
                                {
                                    _listenerBufferPaging[listener]++;
                                }

                                GUI.enabled = true;
                            }
                        }

                        MessageEmissionData[] pagedLocalMessages = component
                            ._registeredListeners[listener]
                            ._emissionBuffer.Reverse()
                            .Skip(page * PageSize)
                            .Take(PageSize)
                            .ToArray();
                        foreach (MessageEmissionData globalEmissionData in pagedLocalMessages)
                        {
                            using (new EditorGUILayout.VerticalScope("box"))
                            {
                                GUIContent labelContent = new("Message Type");
                                GUIContent valueContent = new(
                                    globalEmissionData.message.MessageType.Name,
                                    globalEmissionData.stackTrace
                                );

                                EditorGUILayout.LabelField(labelContent, valueContent);

                                InstanceId? context = globalEmissionData.context;
                                if (context?.Object != null)
                                {
                                    Object unityObject = context.Value.Object;
                                    string label = "Context";
                                    if (
                                        typeof(ITargetedMessage).IsAssignableFrom(
                                            globalEmissionData.message.MessageType
                                        )
                                    )
                                    {
                                        label = "Target";
                                    }
                                    else if (
                                        typeof(IBroadcastMessage).IsAssignableFrom(
                                            globalEmissionData.message.MessageType
                                        )
                                    )
                                    {
                                        label = "Source";
                                    }
                                    EditorGUILayout.ObjectField(
                                        label,
                                        unityObject,
                                        typeof(Object),
                                        true
                                    );
                                }
                            }
                        }
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Listeners", EditorStyles.boldLabel);

            foreach (MonoBehaviour listener in listeners)
            {
                if (listener == null)
                {
                    continue;
                }

                if (
                    !component._registeredListeners.TryGetValue(
                        listener,
                        out MessageRegistrationToken token
                    )
                )
                {
                    continue;
                }

                _listenerFoldouts.TryAdd(listener, false);
                _listenerFoldouts[listener] = EditorGUILayout.Foldout(
                    _listenerFoldouts[listener],
                    listener.GetType().Name,
                    true
                );
                if (_listenerFoldouts[listener])
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.ObjectField("Listener", listener, typeof(MonoBehaviour), true);
                    token.DiagnosticMode = EditorGUILayout.Toggle(
                        "Enable Diagnostics",
                        token.DiagnosticMode
                    );
                    if (token.DiagnosticMode)
                    {
                        if (token._metadata.Count == 0)
                        {
                            EditorGUILayout.LabelField("No messages registered for this listener.");
                        }
                        else
                        {
                            DrawPaginatedRegistrations(listener, token);
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "Enable diagnostics to view registration details.",
                            MessageType.Info
                        );
                    }

                    EditorGUI.indentLevel--;
                }
            }
        }

        private void DrawPaginatedRegistrations(
            MonoBehaviour listener,
            MessageRegistrationToken token
        )
        {
            _listenerRegistrationPaging.TryAdd(listener, 0);
            int page = _listenerRegistrationPaging[listener];
            int totalRegistrations = token._metadata.Count;
            int totalPages = (totalRegistrations + PageSize - 1) / PageSize;
            page = Mathf.Clamp(page, 0, totalPages - 1);

            EditorGUILayout.LabelField("Registrations", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            if (totalPages > 1)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = page > 0;
                    if (GUILayout.Button("<< Previous"))
                    {
                        _listenerRegistrationPaging[listener]--;
                    }
                    GUI.enabled = true;

                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"Page {page + 1} of {totalPages}");
                    GUILayout.FlexibleSpace();

                    GUI.enabled = page < totalPages - 1;
                    if (GUILayout.Button("Next >>"))
                    {
                        _listenerRegistrationPaging[listener]++;
                    }
                    GUI.enabled = true;
                }
            }

            KeyValuePair<
                MessageRegistrationHandle,
                MessageRegistrationMetadata
            >[] pagedRegistrations = token
                ._metadata.OrderBy(kvp => kvp.Key)
                .Skip(page * PageSize)
                .Take(PageSize)
                .ToArray();

            foreach (
                (
                    MessageRegistrationHandle handle,
                    MessageRegistrationMetadata metadata
                ) in pagedRegistrations
            )
            {
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    int callCount = token._callCounts.GetValueOrDefault(handle, 0);

                    string messageName = metadata.type?.Name ?? string.Empty;
                    GUIContent labelContent = new(messageName, $"Priority: {metadata.priority}");
                    GUIContent valueContent = new(
                        metadata.registrationType.ToString(),
                        $"Priority: {metadata.priority}"
                    );

                    EditorGUILayout.LabelField(labelContent, valueContent);
                    if (metadata.context?.Object != null)
                    {
                        Object unityObject = metadata.context.Value.Object;
                        string label = "Context";
                        if (typeof(ITargetedMessage).IsAssignableFrom(metadata.type))
                        {
                            label = "Target";
                        }
                        else if (typeof(IBroadcastMessage).IsAssignableFrom(metadata.type))
                        {
                            label = "Source";
                        }
                        EditorGUILayout.ObjectField(label, unityObject, typeof(Object), true);
                    }

                    EditorGUILayout.LabelField("Call Count", callCount.ToString());
                    EditorGUILayout.Space();
                }
            }
            EditorGUI.indentLevel--;
        }
    }
#endif
}
