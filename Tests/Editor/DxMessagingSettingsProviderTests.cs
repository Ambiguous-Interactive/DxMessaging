#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System.Collections.Generic;
    using DxMessaging.Editor.Settings;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEditor.UIElements;
    using UnityEngine;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    [TestFixture]
    public sealed class DxMessagingSettingsProviderTests
    {
        private readonly List<Object> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object instance in _createdObjects)
            {
                if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }
            }
            _createdObjects.Clear();
        }

        [Test]
        public void BuildSettingsUiCreatesDesignSystemSectionsAndBoundFields()
        {
            DxMessagingSettings settings = CreateSettings();
            SerializedObject serializedSettings = new(settings);
            VisualElement root = new();

            DxMessagingSettingsProvider.BuildSettingsUi(root, serializedSettings);

            Assert.That(root.ClassListContains(DxMessagingSettingsProvider.RootClassName), Is.True);
            List<VisualElement> sections = root.Query<VisualElement>(
                    className: DxMessagingSettingsProvider.SectionClassName
                )
                .ToList();
            Assert.That(sections.Count, Is.EqualTo(3));
            AssertBoundField(root, nameof(DxMessagingSettings._diagnosticsTargets));
            AssertBoundField(root, nameof(DxMessagingSettings._messageBufferSize));
            AssertBoundField(root, nameof(DxMessagingSettings._suppressDomainReloadWarning));
            AssertToggle(root, nameof(DxMessagingSettings._baseCallCheckEnabled), true);
            AssertToggle(root, nameof(DxMessagingSettings._useConsoleBridge), false);
        }

        [Test]
        public void BuildSettingsUiClearsPreviousChildrenBeforeRebuild()
        {
            DxMessagingSettings settings = CreateSettings();
            SerializedObject serializedSettings = new(settings);
            VisualElement root = new();
            root.Add(new Label("Stale child"));

            DxMessagingSettingsProvider.BuildSettingsUi(root, serializedSettings);
            DxMessagingSettingsProvider.BuildSettingsUi(root, serializedSettings);

            List<PropertyField> fields = root.Query<PropertyField>().ToList();
            List<Label> labels = root.Query<Label>().ToList();
            Assert.That(fields.Count, Is.EqualTo(3));
            Assert.That(labels.Exists(label => label.text == "Stale child"), Is.False);
        }

        [Test]
        public void InspectorCheckTogglesApplyThroughSettingsProperties()
        {
            DxMessagingSettings settings = CreateSettings();
            SerializedObject serializedSettings = new(settings);
            VisualElement root = new();

            DxMessagingSettingsProvider.BuildSettingsUi(root, serializedSettings);

            Toggle baseCallToggle = root.Q<Toggle>(
                nameof(DxMessagingSettings._baseCallCheckEnabled)
            );
            Toggle consoleBridgeToggle = root.Q<Toggle>(
                nameof(DxMessagingSettings._useConsoleBridge)
            );

            Assert.That(baseCallToggle, Is.Not.Null);
            Assert.That(consoleBridgeToggle, Is.Not.Null);

            DxMessagingSettingsProvider.ApplySettingsToggleValue(
                serializedSettings,
                nameof(DxMessagingSettings._baseCallCheckEnabled),
                false
            );
            DxMessagingSettingsProvider.ApplySettingsToggleValue(
                serializedSettings,
                nameof(DxMessagingSettings._useConsoleBridge),
                true
            );

            Assert.That(settings.BaseCallCheckEnabled, Is.False);
            Assert.That(settings.UseConsoleBridge, Is.True);
        }

        [Test]
        public void CreateProviderIncludesSectionAndInspectorCheckSearchKeywords()
        {
            SettingsProvider provider =
                DxMessagingSettingsProvider.CreateDxMessagingSettingsProvider();

            Assert.That(provider.keywords, Does.Contain("Diagnostics"));
            Assert.That(provider.keywords, Does.Contain("Editor Safety"));
            Assert.That(provider.keywords, Does.Contain("Inspector Checks"));
            Assert.That(provider.keywords, Does.Contain("Base-Call Check Enabled"));
            Assert.That(provider.keywords, Does.Contain("Use Console Bridge"));
        }

        private DxMessagingSettings CreateSettings()
        {
            DxMessagingSettings settings = ScriptableObject.CreateInstance<DxMessagingSettings>();
            _createdObjects.Add(settings);
            return settings;
        }

        private static void AssertBoundField(VisualElement root, string propertyName)
        {
            PropertyField field = root.Q<PropertyField>(propertyName);
            Assert.That(field, Is.Not.Null, $"Missing PropertyField for {propertyName}.");
            Assert.That(field.bindingPath, Is.EqualTo(propertyName));
            Assert.That(
                field.ClassListContains(DxMessagingSettingsProvider.FieldClassName),
                Is.True
            );
            Assert.That(field.tooltip, Is.Not.Empty);
        }

        private static void AssertToggle(
            VisualElement root,
            string toggleName,
            bool expectedInitialValue
        )
        {
            Toggle toggle = root.Q<Toggle>(toggleName);
            Assert.That(toggle, Is.Not.Null, $"Missing Toggle for {toggleName}.");
            Assert.That(toggle.value, Is.EqualTo(expectedInitialValue));
            Assert.That(
                toggle.ClassListContains(DxMessagingSettingsProvider.FieldClassName),
                Is.True
            );
            Assert.That(toggle.tooltip, Is.Not.Empty);
        }
    }
}
#endif
