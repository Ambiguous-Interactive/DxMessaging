namespace WallstopStudios.DxMessagingSamples.DiagnosticsToolingExerciser
{
    using System;
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using UnityEngine;

    [DisallowMultipleComponent]
    public sealed class DiagnosticsToolingExerciser : MonoBehaviour
    {
        [SerializeField]
        private DiagnosticsToolingReceiver[] receivers = Array.Empty<DiagnosticsToolingReceiver>();

        [SerializeField]
        private GameObject[] broadcastSources = Array.Empty<GameObject>();

        [SerializeField]
        private bool enableGlobalDiagnostics = true;

        [SerializeField]
        private bool emitOnStart = true;

        [SerializeField]
        private int burstCount = 3;

        [SerializeField]
        private float repeatSeconds;

        [SerializeField]
        private bool logSummary = true;

        [SerializeField]
        private int sequence;

        [SerializeField]
        private string lastRunSummary = "Not run yet";

        public int Sequence => sequence;

        public string LastRunSummary => lastRunSummary;

        private void Start()
        {
            ConfigureDiagnostics();

            if (emitOnStart)
            {
                StartCoroutine(EmitAfterSceneStartup());
            }
            else
            {
                StartRepeatingIfRequested();
            }
        }

        private IEnumerator EmitAfterSceneStartup()
        {
            yield return null;
            EmitBurst();
            StartRepeatingIfRequested();
        }

        private void StartRepeatingIfRequested()
        {
            if (repeatSeconds > 0)
            {
                InvokeRepeating(nameof(EmitBurst), repeatSeconds, repeatSeconds);
            }
        }

        [ContextMenu("Emit Burst")]
        public void EmitBurst()
        {
            int count = Mathf.Max(1, burstCount);
            for (int index = 0; index < count; index++)
            {
                EmitOneOfEach();
            }
        }

        [ContextMenu("Emit One Of Each")]
        public void EmitOneOfEach()
        {
            EnsureReceiversReady();
            sequence++;

            ToolingPulse pulse = new(
                CreateTraceId("pulse"),
                "Diagnostics sample global pulse",
                sequence
            );
            pulse.EmitUntargeted();

            int targetedEmits = 0;
            foreach (DiagnosticsToolingReceiver receiver in receivers)
            {
                if (receiver == null)
                {
                    continue;
                }

                ToolingCommand command = new(
                    CreateTraceId("target"),
                    $"Command for {receiver.ListenerLabel}",
                    sequence
                );
                command.EmitGameObjectTargeted(receiver.gameObject);
                targetedEmits++;
            }

            int broadcastEmits = 0;
            GameObject[] sources =
                broadcastSources == null || broadcastSources.Length == 0
                    ? new[] { gameObject }
                    : broadcastSources;
            foreach (GameObject source in sources)
            {
                if (source == null)
                {
                    continue;
                }

                ToolingSignal signal = new(CreateTraceId("signal"), source.name, sequence);
                InstanceId sourceId = source;
                MessageHandler.MessageBus.SourcedBroadcast(ref sourceId, ref signal);
                broadcastEmits++;
            }

            lastRunSummary =
                $"Sequence {sequence}: 1 untargeted, {targetedEmits} targeted, {broadcastEmits} broadcast";

            if (logSummary)
            {
                Debug.Log(lastRunSummary, this);
            }
        }

        private void ConfigureDiagnostics()
        {
            if (!enableGlobalDiagnostics)
            {
                return;
            }

            if (MessageHandler.MessageBus is MessageBus messageBus)
            {
                messageBus.DiagnosticsMode = true;
            }
            else
            {
                Debug.LogWarning(
                    "DxMessaging global diagnostics could not be enabled because the active global bus is not the default MessageBus.",
                    this
                );
            }
        }

        private string CreateTraceId(string route)
        {
            return $"sample-{route}-{sequence:000}";
        }

        private void EnsureReceiversReady()
        {
            foreach (DiagnosticsToolingReceiver receiver in receivers)
            {
                receiver?.EnsureToolingRegistrations();
            }
        }
    }
}
