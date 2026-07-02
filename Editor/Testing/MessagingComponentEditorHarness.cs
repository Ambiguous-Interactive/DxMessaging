#if UNITY_EDITOR
namespace DxMessaging.Editor.Testing
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Core;
    using Core.Diagnostics;
    using Core.MessageBus;
    using DxMessaging.Unity;
    using UnityEngine;

    /// <summary>
    /// Captures inspector-oriented diagnostics for <see cref="MessagingComponent"/> instances without relying on GUI APIs.
    /// Intended for automated editor tests that need to validate inspector state transitions.
    /// </summary>
    internal static class MessagingComponentEditorHarness
    {
        private static readonly MessageEmissionData[] EmptyEmissions =
            Array.Empty<MessageEmissionData>();

        internal static MessagingComponentInspectorState Capture(
            MessagingComponent component,
            bool resolveSerializedProviderBus = true
        )
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            bool globalDiagnosticsEnabled = false;
            IReadOnlyList<MessageEmissionData> globalHistory = EmptyEmissions;

            if (MessageHandler.MessageBus is MessageBus concreteBus)
            {
                globalDiagnosticsEnabled = concreteBus.DiagnosticsMode;
                if (globalDiagnosticsEnabled && concreteBus._emissionBuffer.Count > 0)
                {
                    globalHistory = concreteBus._emissionBuffer.ToArray();
                }
            }

            List<ListenerDiagnosticsView> listenerViews = component
                ._registeredListeners.OrderBy(pair => InstanceId.StableId(pair.Key))
                .Select(pair => CreateListenerView(pair.Key, pair.Value))
                .ToList();

            ProviderDiagnosticsView providerDiagnostics = CreateProviderDiagnostics(
                component,
                resolveSerializedProviderBus
            );

            return new MessagingComponentInspectorState(
                globalDiagnosticsEnabled,
                globalHistory,
                listenerViews,
                providerDiagnostics
            );
        }

        private static ListenerDiagnosticsView CreateListenerView(
            MonoBehaviour listener,
            MessageRegistrationToken token
        )
        {
            MessageRegistrationView[] registrations = token
                ._metadata.OrderBy(pair => pair.Key)
                .Select(pair => new MessageRegistrationView(
                    pair.Key,
                    pair.Value,
                    token._callCounts.TryGetValue(pair.Key, out int callCount) ? callCount : 0
                ))
                .ToArray();

            IReadOnlyList<MessageEmissionData> emissionHistory =
                token._emissionBuffer.Count > 0 ? token._emissionBuffer.ToArray() : EmptyEmissions;

            return new ListenerDiagnosticsView(
                listener,
                token.DiagnosticMode,
                token.Enabled,
                registrations,
                emissionHistory
            );
        }

        internal static ProviderDiagnosticsView CreateProviderDiagnostics(
            MessagingComponent component,
            bool resolveSerializedProviderBus = true
        )
        {
            bool autoConfigure = component.AutoConfigureSerializedProviderOnAwake;
            bool hasSerializedProvider =
                component.SerializedProviderAsset != null || component.HasSerializedProvider;
            bool hasRuntimeProvider = component.HasRuntimeProvider;
            bool hasMessageBusOverride = component.HasMessageBusOverride;
            bool serializedProviderMissingWarning = autoConfigure && !hasSerializedProvider;

            bool serializedProviderNullBusWarning = false;
            if (hasSerializedProvider && resolveSerializedProviderBus)
            {
                IMessageBus resolvedBus = component.SerializedProviderHandle.ResolveBus();
                serializedProviderNullBusWarning = resolvedBus == null;
            }

            return new ProviderDiagnosticsView(
                autoConfigure,
                hasSerializedProvider,
                hasRuntimeProvider,
                hasMessageBusOverride,
                serializedProviderMissingWarning,
                serializedProviderNullBusWarning
            );
        }
    }

    internal sealed class MessagingComponentInspectorState
    {
        internal MessagingComponentInspectorState(
            bool globalDiagnosticsEnabled,
            IReadOnlyList<MessageEmissionData> globalEmissionHistory,
            IReadOnlyList<ListenerDiagnosticsView> listeners,
            ProviderDiagnosticsView providerDiagnostics
        )
        {
            GlobalDiagnosticsEnabled = globalDiagnosticsEnabled;
            GlobalEmissionHistory =
                globalEmissionHistory
                ?? throw new ArgumentNullException(nameof(globalEmissionHistory));
            Listeners = listeners ?? throw new ArgumentNullException(nameof(listeners));
            ProviderDiagnostics = providerDiagnostics;
        }

        internal bool GlobalDiagnosticsEnabled { get; }

        internal IReadOnlyList<MessageEmissionData> GlobalEmissionHistory { get; }

        internal IReadOnlyList<ListenerDiagnosticsView> Listeners { get; }

        internal ProviderDiagnosticsView ProviderDiagnostics { get; }
    }

    internal sealed class ListenerDiagnosticsView
    {
        internal ListenerDiagnosticsView(
            MonoBehaviour listener,
            bool diagnosticsEnabled,
            bool tokenEnabled,
            IReadOnlyList<MessageRegistrationView> registrations,
            IReadOnlyList<MessageEmissionData> emissionHistory
        )
        {
            Listener = listener;
            DiagnosticsEnabled = diagnosticsEnabled;
            TokenEnabled = tokenEnabled;
            Registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
            EmissionHistory =
                emissionHistory ?? throw new ArgumentNullException(nameof(emissionHistory));
        }

        internal MonoBehaviour Listener { get; }

        internal bool DiagnosticsEnabled { get; }

        internal bool TokenEnabled { get; }

        internal IReadOnlyList<MessageRegistrationView> Registrations { get; }

        internal IReadOnlyList<MessageEmissionData> EmissionHistory { get; }
    }

    internal readonly struct MessageRegistrationView
    {
        internal MessageRegistrationView(
            MessageRegistrationHandle handle,
            MessageRegistrationMetadata metadata,
            int callCount
        )
        {
            Handle = handle;
            Metadata = metadata;
            CallCount = callCount;
        }

        internal MessageRegistrationHandle Handle { get; }

        internal MessageRegistrationMetadata Metadata { get; }

        internal int CallCount { get; }
    }

    internal readonly struct ProviderDiagnosticsView
    {
        internal ProviderDiagnosticsView(
            bool autoConfigureSerializedProviderOnAwake,
            bool hasSerializedProvider,
            bool hasRuntimeProvider,
            bool hasMessageBusOverride,
            bool serializedProviderMissingWarning,
            bool serializedProviderNullBusWarning
        )
        {
            AutoConfigureSerializedProviderOnAwake = autoConfigureSerializedProviderOnAwake;
            HasSerializedProvider = hasSerializedProvider;
            HasRuntimeProvider = hasRuntimeProvider;
            HasMessageBusOverride = hasMessageBusOverride;
            SerializedProviderMissingWarning = serializedProviderMissingWarning;
            SerializedProviderNullBusWarning = serializedProviderNullBusWarning;
        }

        internal bool AutoConfigureSerializedProviderOnAwake { get; }

        internal bool HasSerializedProvider { get; }

        internal bool HasRuntimeProvider { get; }

        internal bool HasMessageBusOverride { get; }

        internal bool SerializedProviderMissingWarning { get; }

        internal bool SerializedProviderNullBusWarning { get; }
    }
}
#endif
