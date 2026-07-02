namespace DxMessaging.Editor.Settings
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using Core.MessageBus;
    using DxMessaging.Editor;
    using UnityEditor;
    using UnityEditor.UIElements;
    using UnityEngine;
    using UnityEngine.UIElements;

    /// <summary>
    /// Project Settings provider for DxMessaging configuration.
    /// </summary>
    /// <remarks>
    /// Exposes toggles for global diagnostics mode and message buffer size under Project Settings > Wallstop Studios > DxMessaging.
    /// </remarks>
    public sealed class DxMessagingSettingsProvider : SettingsProvider
    {
        internal const string RootClassName = "dxmessaging-settings";
        internal const string SectionClassName = "dxmessaging-settings-section";
        internal const string FieldClassName = "dxmessaging-settings-field";

        private const string DiagnosticsSectionTitle = "Diagnostics";
        private const string DiagnosticsSectionDescription =
            "Controls how much message history the editor keeps when diagnostics are enabled.";
        private const string DiagnosticsTargetsLabel = "Diagnostics Targets";
        private const string DiagnosticsTargetsTooltip =
            "Select where global diagnostics should be enabled by default.";
        private const string MessageBufferSizeLabel = "Message Buffer Size";
        private const string MessageBufferSizeTooltip =
            "Number of emissions kept per bus/token when diagnostics mode is active.";
        private const string EditorSafetySectionTitle = "Editor Safety";
        private const string EditorSafetySectionDescription =
            "Keeps Unity editor warnings intentional while preserving DxMessaging reset behavior.";
        private const string SuppressDomainReloadWarningLabel = "Suppress Domain Reload Warning";
        private const string SuppressDomainReloadWarningTooltip =
            "Disable the warning shown when Enter Play Mode Options skips domain reload.";
        private const string InspectorChecksSectionTitle = "Inspector Checks";
        private const string InspectorChecksSectionDescription =
            "Controls Inspector warnings and the optional console bridge for base-call diagnostics.";
        private const string BaseCallCheckEnabledLabel = "Base-Call Check Enabled";
        private const string BaseCallCheckEnabledTooltip =
            "Show Inspector warnings when MessageAwareComponent overrides omit base.RegisterMessageHandlers().";
        private const string UseConsoleBridgeLabel = "Use Console Bridge";
        private const string UseConsoleBridgeTooltip =
            "Also harvest compiler/analyzer warnings from Unity's console in addition to the IL scanner.";

        private static readonly Color AccentColor = DxMessagingEditorPalette.Amber;
        private static readonly Color SectionBorderColor = DxMessagingEditorPalette.BorderPanel;

        private SerializedObject _messagingSettings;
        private bool _uiToolkitActivated;

        private DxMessagingSettingsProvider(
            string path,
            SettingsScope scope = SettingsScope.Project
        )
            : base(path, scope) { }

        /// <summary>
        /// Initializes the serialized settings backing store when the settings page is opened.
        /// </summary>
        /// <param name="searchContext">Search text provided by the Project Settings window.</param>
        /// <param name="rootElement">Root visual element for UI Toolkit-based providers.</param>
        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            _messagingSettings = DxMessagingSettings.GetSerializedSettings();
            BuildSettingsUi(rootElement, _messagingSettings);
            _uiToolkitActivated = true;
        }

        /// <summary>
        /// Renders the DxMessaging settings UI and persists any modifications.
        /// </summary>
        /// <param name="searchContext">Search text provided by the Project Settings window.</param>
        public override void OnGUI(string searchContext)
        {
            if (_uiToolkitActivated)
            {
                return;
            }

            _messagingSettings ??= DxMessagingSettings.GetSerializedSettings();

            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 240f;
            try
            {
                DrawSectionHeader(DiagnosticsSectionTitle, DiagnosticsSectionDescription);
                DrawDiagnosticsTargetsField(_messagingSettings);
                DrawPropertyField(
                    _messagingSettings,
                    nameof(DxMessagingSettings._messageBufferSize),
                    MessageBufferSizeLabel,
                    MessageBufferSizeTooltip
                );

                DrawSectionHeader(EditorSafetySectionTitle, EditorSafetySectionDescription);
                DrawPropertyField(
                    _messagingSettings,
                    nameof(DxMessagingSettings._suppressDomainReloadWarning),
                    SuppressDomainReloadWarningLabel,
                    SuppressDomainReloadWarningTooltip
                );

                DrawSectionHeader(InspectorChecksSectionTitle, InspectorChecksSectionDescription);
                DrawSettingsToggle(
                    _messagingSettings,
                    nameof(DxMessagingSettings._baseCallCheckEnabled),
                    BaseCallCheckEnabledLabel,
                    BaseCallCheckEnabledTooltip,
                    settings => settings.BaseCallCheckEnabled
                );
                DrawSettingsToggle(
                    _messagingSettings,
                    nameof(DxMessagingSettings._useConsoleBridge),
                    UseConsoleBridgeLabel,
                    UseConsoleBridgeTooltip,
                    settings => settings.UseConsoleBridge
                );

                _messagingSettings.ApplyModifiedProperties();
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }
        }

        internal static void BuildSettingsUi(
            VisualElement rootElement,
            SerializedObject serializedSettings
        )
        {
            if (rootElement == null)
            {
                throw new ArgumentNullException(nameof(rootElement));
            }
            if (serializedSettings == null)
            {
                throw new ArgumentNullException(nameof(serializedSettings));
            }

            rootElement.Clear();
            rootElement.AddToClassList(RootClassName);
            rootElement.style.maxWidth = 720;
            rootElement.style.paddingTop = 10;
            rootElement.style.paddingRight = 14;
            rootElement.style.paddingBottom = 10;
            rootElement.style.paddingLeft = 14;

            Label title = new("DxMessaging");
            title.style.fontSize = 19;
            title.style.marginBottom = 3;
            rootElement.Add(title);

            Label subtitle = new(
                "Project-wide defaults for diagnostics capture and editor safety checks."
            );
            subtitle.style.marginBottom = 12;
            rootElement.Add(subtitle);

            rootElement.Add(
                CreateSection(
                    DiagnosticsSectionTitle,
                    DiagnosticsSectionDescription,
                    CreatePropertyField(
                        serializedSettings,
                        nameof(DxMessagingSettings._diagnosticsTargets),
                        DiagnosticsTargetsLabel,
                        DiagnosticsTargetsTooltip
                    ),
                    CreatePropertyField(
                        serializedSettings,
                        nameof(DxMessagingSettings._messageBufferSize),
                        MessageBufferSizeLabel,
                        MessageBufferSizeTooltip
                    )
                )
            );

            rootElement.Add(
                CreateSection(
                    EditorSafetySectionTitle,
                    EditorSafetySectionDescription,
                    CreatePropertyField(
                        serializedSettings,
                        nameof(DxMessagingSettings._suppressDomainReloadWarning),
                        SuppressDomainReloadWarningLabel,
                        SuppressDomainReloadWarningTooltip
                    )
                )
            );

            rootElement.Add(
                CreateSection(
                    InspectorChecksSectionTitle,
                    InspectorChecksSectionDescription,
                    CreateSettingsToggle(
                        serializedSettings,
                        nameof(DxMessagingSettings._baseCallCheckEnabled),
                        BaseCallCheckEnabledLabel,
                        BaseCallCheckEnabledTooltip,
                        settings => settings.BaseCallCheckEnabled
                    ),
                    CreateSettingsToggle(
                        serializedSettings,
                        nameof(DxMessagingSettings._useConsoleBridge),
                        UseConsoleBridgeLabel,
                        UseConsoleBridgeTooltip,
                        settings => settings.UseConsoleBridge
                    )
                )
            );

            rootElement.Bind(serializedSettings);
        }

        private static VisualElement CreateSection(
            string title,
            string description,
            params VisualElement[] fields
        )
        {
            VisualElement section = new();
            section.AddToClassList(SectionClassName);
            section.style.borderLeftWidth = 3;
            section.style.borderLeftColor = AccentColor;
            section.style.borderTopWidth = 1;
            section.style.borderTopColor = SectionBorderColor;
            section.style.marginBottom = 10;
            section.style.paddingTop = 10;
            section.style.paddingRight = 10;
            section.style.paddingBottom = 10;
            section.style.paddingLeft = 12;

            Label heading = new(title);
            heading.style.fontSize = 13;
            heading.style.marginBottom = 2;
            section.Add(heading);

            Label body = new(description);
            body.style.marginBottom = 8;
            section.Add(body);

            foreach (VisualElement field in fields)
            {
                section.Add(field);
            }
            return section;
        }

        private static PropertyField CreatePropertyField(
            SerializedObject serializedSettings,
            string propertyName,
            string label,
            string tooltip
        )
        {
            SerializedProperty property = serializedSettings.FindProperty(propertyName);
            if (property == null)
            {
                throw new MissingFieldException(typeof(DxMessagingSettings).FullName, propertyName);
            }

            PropertyField field = new(property, label)
            {
                name = propertyName,
                bindingPath = property.propertyPath,
                tooltip = tooltip,
            };
            field.AddToClassList(FieldClassName);
            field.style.marginTop = 4;
            return field;
        }

        private static Toggle CreateSettingsToggle(
            SerializedObject serializedSettings,
            string fieldName,
            string label,
            string tooltip,
            Func<DxMessagingSettings, bool> getValue
        )
        {
            if (serializedSettings.targetObject is not DxMessagingSettings settings)
            {
                throw new ArgumentException(
                    $"Expected {nameof(DxMessagingSettings)} target.",
                    nameof(serializedSettings)
                );
            }

            Toggle toggle = new(label) { name = fieldName, tooltip = tooltip };
            toggle.AddToClassList(FieldClassName);
            toggle.style.marginTop = 4;
            toggle.SetValueWithoutNotify(getValue(settings));
            toggle.RegisterValueChangedCallback(evt =>
                ApplySettingsToggleValue(serializedSettings, fieldName, evt.newValue)
            );
            return toggle;
        }

        private static void DrawSectionHeader(string title, string description)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(description, EditorStyles.wordWrappedMiniLabel);
        }

        private static void DrawDiagnosticsTargetsField(SerializedObject serializedSettings)
        {
            SerializedProperty targetsProp = serializedSettings.FindProperty(
                nameof(DxMessagingSettings._diagnosticsTargets)
            );
            DiagnosticsTarget currentTargets = (DiagnosticsTarget)targetsProp.enumValueFlag;
            DiagnosticsTarget updatedTargets = (DiagnosticsTarget)
                EditorGUILayout.EnumFlagsField(
                    new GUIContent(DiagnosticsTargetsLabel, DiagnosticsTargetsTooltip),
                    currentTargets
                );
            if (updatedTargets != currentTargets)
            {
                targetsProp.enumValueFlag = (int)updatedTargets;
            }
        }

        private static void DrawPropertyField(
            SerializedObject serializedSettings,
            string propertyName,
            string label,
            string tooltip
        )
        {
            EditorGUILayout.PropertyField(
                serializedSettings.FindProperty(propertyName),
                new GUIContent(label, tooltip)
            );
        }

        private static void DrawSettingsToggle(
            SerializedObject serializedSettings,
            string fieldName,
            string label,
            string tooltip,
            Func<DxMessagingSettings, bool> getValue
        )
        {
            if (serializedSettings.targetObject is not DxMessagingSettings settings)
            {
                throw new ArgumentException(
                    $"Expected {nameof(DxMessagingSettings)} target.",
                    nameof(serializedSettings)
                );
            }

            bool currentValue = getValue(settings);
            bool updatedValue = EditorGUILayout.Toggle(
                new GUIContent(label, tooltip),
                currentValue
            );
            if (updatedValue != currentValue)
            {
                serializedSettings.ApplyModifiedProperties();
                ApplySettingsToggleValue(serializedSettings, fieldName, updatedValue);
            }
        }

        internal static void ApplySettingsToggleValue(
            SerializedObject serializedSettings,
            string fieldName,
            bool value
        )
        {
            if (serializedSettings.targetObject is not DxMessagingSettings settings)
            {
                throw new ArgumentException(
                    $"Expected {nameof(DxMessagingSettings)} target.",
                    nameof(serializedSettings)
                );
            }

            switch (fieldName)
            {
                case nameof(DxMessagingSettings._baseCallCheckEnabled):
                    settings.BaseCallCheckEnabled = value;
                    break;
                case nameof(DxMessagingSettings._useConsoleBridge):
                    settings.UseConsoleBridge = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fieldName), fieldName, null);
            }

            EditorUtility.SetDirty(settings);
            serializedSettings.Update();
        }

        /// <summary>
        /// Factory used by Unity to register the DxMessaging project settings page.
        /// </summary>
        /// <returns>Configured settings provider instance.</returns>
        [SettingsProvider]
        public static SettingsProvider CreateDxMessagingSettingsProvider()
        {
            DxMessagingSettingsProvider provider = new("Project/Wallstop Studios/DxMessaging")
            {
                keywords = new HashSet<string>(
                    new[]
                    {
                        "DxMessaging",
                        "Diagnostics",
                        "MessageBus",
                        "Targets",
                        DiagnosticsTargetsLabel,
                        MessageBufferSizeLabel,
                        EditorSafetySectionTitle,
                        SuppressDomainReloadWarningLabel,
                        InspectorChecksSectionTitle,
                        BaseCallCheckEnabledLabel,
                        UseConsoleBridgeLabel,
                        "Wallstop",
                        "Wallstop Studios",
                    }
                ),
            };

            return provider;
        }
    }

#endif
}
