namespace WallstopStudios.DxMessagingSamples.DiagnosticsToolingExerciser
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
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

        [SerializeField]
        private int separateBusCallCount;

        [SerializeField]
        private int separateBusSequence;

        [SerializeField]
        private string lastSeparateBusTraceId = "None";

        private static readonly HashSet<int> InitializedRunnerIds = new();
        private static MessageBus diagnosticsBus;
        private static int diagnosticsLeaseCount;
        private static bool originalDiagnosticsMode;

        private bool hasDiagnosticsLease;
        private MessageBus separateMessageBus;
        private MessageHandler standaloneMessageHandler;
        private MessageRegistrationToken standaloneToken;

        public int Sequence => sequence;

        public string LastRunSummary => lastRunSummary;

        public int SeparateBusCallCount => separateBusCallCount;

        public int SeparateBusRegistrationCount => separateMessageBus?.RegisteredUntargeted ?? 0;

        public int SeparateBusLogCount => separateMessageBus?.Log.Registrations.Count ?? 0;

        public bool StandaloneTokenEnabled => standaloneToken?.Enabled ?? false;

        public string LastSeparateBusTraceId => lastSeparateBusTraceId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void BeginPlayGeneration()
        {
            RestoreDiagnosticsMode();
            InitializedRunnerIds.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InitializeAfterSceneLoad()
        {
#if UNITY_6000_5_OR_NEWER
            DiagnosticsToolingExerciser[] runners = FindObjectsByType<DiagnosticsToolingExerciser>(
                FindObjectsInactive.Exclude
            );
#elif UNITY_2023_1_OR_NEWER
            DiagnosticsToolingExerciser[] runners = FindObjectsByType<DiagnosticsToolingExerciser>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );
#else
            DiagnosticsToolingExerciser[] runners =
                FindObjectsOfType<DiagnosticsToolingExerciser>();
#endif
            foreach (DiagnosticsToolingExerciser runner in runners)
            {
                if (runner != null && runner.isActiveAndEnabled)
                {
                    runner.BeginPlaySession();
                }
            }
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                BeginPlaySession();
            }
        }

        private void OnDisable()
        {
            _ = InitializedRunnerIds.Remove(((InstanceId)this).Id);
            CancelInvoke(nameof(EmitBurst));
            StopAllCoroutines();
            ReleaseDiagnosticsLease();
            ReleaseStandaloneDiagnostics();
        }

        private void BeginPlaySession()
        {
            if (!InitializedRunnerIds.Add(((InstanceId)this).Id))
            {
                return;
            }

            CancelInvoke(nameof(EmitBurst));
            StopAllCoroutines();
            sequence = 0;
            lastRunSummary = "Not run yet";
            separateBusCallCount = 0;
            separateBusSequence = 0;
            lastSeparateBusTraceId = "None";
            foreach (DiagnosticsToolingReceiver receiver in receivers)
            {
                if (receiver != null)
                {
                    receiver.ResetCounts();
                }
            }

            ConfigureDiagnostics();
            ConfigureStandaloneDiagnostics();

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
            if (0 < repeatSeconds)
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

        [ContextMenu("Reset Counters And Emit Burst")]
        public void ResetCountersAndEmitBurst()
        {
            foreach (DiagnosticsToolingReceiver receiver in receivers)
            {
                if (receiver != null)
                {
                    receiver.ResetCounts();
                }
            }

            EmitBurst();
        }

        [ContextMenu("Emit One Of Each")]
        public void EmitOneOfEach()
        {
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
            if (broadcastSources == null || broadcastSources.Length == 0)
            {
                EmitSignal(gameObject);
                broadcastEmits = 1;
            }
            else
            {
                foreach (GameObject source in broadcastSources)
                {
                    if (source == null)
                    {
                        continue;
                    }

                    EmitSignal(source);
                    broadcastEmits++;
                }
            }

            lastRunSummary =
                $"Sequence {sequence}: 1 untargeted, {targetedEmits} targeted, {broadcastEmits} broadcast";

            if (logSummary)
            {
                Debug.Log(lastRunSummary, this);
            }
        }

        [ContextMenu("Emit On Separate Bus")]
        public void EmitOnSeparateBus()
        {
            if (separateMessageBus == null || standaloneToken == null)
            {
                lastSeparateBusTraceId = "Enter Play Mode to configure the separate bus";
                return;
            }

            separateBusSequence++;
            ToolingPulse pulse = new(
                $"sample-separate-{separateBusSequence:000}",
                "Separate MessageBus",
                separateBusSequence
            );
            separateMessageBus.UntargetedBroadcast(ref pulse);
        }

        private void EmitSignal(GameObject source)
        {
            ToolingSignal signal = new(CreateTraceId("signal"), source.name, sequence);
            InstanceId sourceId = source;
            MessageHandler.MessageBus.SourcedBroadcast(ref sourceId, ref signal);
        }

        private void ConfigureDiagnostics()
        {
            if (!enableGlobalDiagnostics || hasDiagnosticsLease)
            {
                return;
            }

            if (MessageHandler.MessageBus is MessageBus messageBus)
            {
                if (diagnosticsLeaseCount == 0)
                {
                    diagnosticsBus = messageBus;
                    originalDiagnosticsMode = messageBus.DiagnosticsMode;
                }
                else if (!object.ReferenceEquals(diagnosticsBus, messageBus))
                {
                    Debug.LogWarning(
                        "DxMessaging diagnostics are already scoped to a different message bus.",
                        this
                    );
                    return;
                }

                diagnosticsLeaseCount++;
                hasDiagnosticsLease = true;
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

        private void ReleaseDiagnosticsLease()
        {
            if (!hasDiagnosticsLease)
            {
                return;
            }

            hasDiagnosticsLease = false;
            diagnosticsLeaseCount = Mathf.Max(0, diagnosticsLeaseCount - 1);
            if (diagnosticsLeaseCount == 0)
            {
                RestoreDiagnosticsMode();
            }
        }

        private void ConfigureStandaloneDiagnostics()
        {
            ReleaseStandaloneDiagnostics();

            separateMessageBus = new MessageBus { DiagnosticsMode = true };
            separateMessageBus.Log.Enabled = true;
            standaloneMessageHandler = new MessageHandler((InstanceId)this, separateMessageBus)
            {
                active = true,
            };
            standaloneToken = MessageRegistrationToken.Create(
                standaloneMessageHandler,
                separateMessageBus
            );
            standaloneToken.DiagnosticMode = true;
            _ = standaloneToken.RegisterUntargeted<ToolingPulse>(OnSeparateBusPulse);
            standaloneToken.Enable();
        }

        private void ReleaseStandaloneDiagnostics()
        {
            standaloneToken?.Dispose();
            standaloneToken = null;

            if (standaloneMessageHandler != null)
            {
                standaloneMessageHandler.active = false;
                standaloneMessageHandler = null;
            }

            if (separateMessageBus != null)
            {
                separateMessageBus.DiagnosticsMode = false;
                separateMessageBus.Log.Enabled = false;
                _ = separateMessageBus.Log.Clear();
                separateMessageBus = null;
            }
        }

        private void OnSeparateBusPulse(in ToolingPulse message)
        {
            separateBusCallCount++;
            lastSeparateBusTraceId = message.traceId;
        }

        private static void RestoreDiagnosticsMode()
        {
            if (diagnosticsBus != null)
            {
                diagnosticsBus.DiagnosticsMode = originalDiagnosticsMode;
            }

            diagnosticsBus = null;
            diagnosticsLeaseCount = 0;
            originalDiagnosticsMode = false;
        }

        private string CreateTraceId(string route)
        {
            return $"sample-{route}-{sequence:000}";
        }
    }
}
