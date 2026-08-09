namespace WallstopStudios.DxMessagingSamples.DiagnosticsToolingExerciser
{
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Messages;
    using DxMessaging.Unity;
    using UnityEngine;

    [DisallowMultipleComponent]
    public sealed class DiagnosticsToolingReceiver : MessageAwareComponent
    {
        [SerializeField]
        private string listenerLabel = "Receiver";

        [SerializeField]
        private GameObject broadcastSourceFilter;

        [SerializeField]
        private bool enableLocalDiagnostics = true;

        [SerializeField]
        private bool registerGlobalAcceptAll = true;

        [SerializeField]
        private bool logMessages;

        [SerializeField]
        private int untargetedCount;

        [SerializeField]
        private int targetedCount;

        [SerializeField]
        private int broadcastCount;

        [SerializeField]
        private int globalAcceptAllCount;

        [SerializeField]
        private string lastTraceId = "None";

        [SerializeField]
        private string lastRoute = "None";

        [SerializeField]
        private string lastPayload = "None";

        private static readonly HashSet<int> PreparedReceiverIds = new();

        public string ListenerLabel => listenerLabel;

        public int UntargetedCount => untargetedCount;

        public int TargetedCount => targetedCount;

        public int BroadcastCount => broadcastCount;

        public int GlobalAcceptAllCount => globalAcceptAllCount;

        protected override bool RegisterForStringMessages => false;

        protected override void Awake()
        {
            base.Awake();
            _ = PreparedReceiverIds.Add(((InstanceId)this).Id);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            _ = PreparedReceiverIds.Remove(((InstanceId)this).Id);
            base.OnDestroy();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void BeginPlayGeneration()
        {
            PreparedReceiverIds.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RestoreRegistrationsAfterSceneLoad()
        {
#if UNITY_2023_1_OR_NEWER
            DiagnosticsToolingReceiver[] receivers = FindObjectsByType<DiagnosticsToolingReceiver>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );
#else
            DiagnosticsToolingReceiver[] receivers =
                FindObjectsOfType<DiagnosticsToolingReceiver>();
#endif
            foreach (DiagnosticsToolingReceiver receiver in receivers)
            {
                if (receiver != null && receiver.isActiveAndEnabled)
                {
                    receiver.EnsureToolingRegistrations(force: true);
                    receiver.Token.Enable();
                }
            }
        }

        protected override void OnEnable()
        {
            EnsureToolingRegistrations();
            base.OnEnable();
        }

        private void EnsureToolingRegistrations(bool force = false)
        {
            if (!force && !PreparedReceiverIds.Add(((InstanceId)this).Id))
            {
                return;
            }

            _ = PreparedReceiverIds.Add(((InstanceId)this).Id);
            MessagingComponent messagingComponent = GetComponent<MessagingComponent>();
            messagingComponent.Release(this);
            MessageRegistrationToken liveToken = messagingComponent.Create(this);
            _messagingComponent = messagingComponent;
            _messageRegistrationToken = liveToken;
            RegisterMessageHandlers();
        }

        protected override void RegisterMessageHandlers()
        {
            base.RegisterMessageHandlers();

            if (enableLocalDiagnostics)
            {
                Token.DiagnosticMode = true;
            }

            _ = Token.RegisterUntargeted<ToolingPulse>(OnPulse);
            _ = Token.RegisterGameObjectTargeted<ToolingCommand>(gameObject, OnCommand);
            _ = Token.RegisterBroadcastWithoutSource<ToolingSignal>(OnSignalFromAnySource);

            if (broadcastSourceFilter != null)
            {
                InstanceId source = broadcastSourceFilter;
                _ = Token.RegisterBroadcast<ToolingSignal>(source, OnSignalFromExactSource);
            }

            if (registerGlobalAcceptAll)
            {
                _ = Token.RegisterGlobalAcceptAll(OnAnyUntargeted, OnAnyTargeted, OnAnyBroadcast);
            }
        }

        [ContextMenu("Reset Counts")]
        public void ResetCounts()
        {
            untargetedCount = 0;
            targetedCount = 0;
            broadcastCount = 0;
            globalAcceptAllCount = 0;
            lastTraceId = "None";
            lastRoute = "None";
            lastPayload = "None";
        }

        private void OnPulse(ref ToolingPulse message)
        {
            untargetedCount++;
            Record("Untargeted", message.traceId, message.channel);
        }

        private void OnCommand(ref ToolingCommand message)
        {
            targetedCount++;
            Record("Targeted", message.traceId, message.command);
        }

        private void OnSignalFromAnySource(InstanceId source, ToolingSignal message)
        {
            broadcastCount++;
            Record("Broadcast without source", message.traceId, message.sourceLabel);
        }

        private void OnSignalFromExactSource(ref ToolingSignal message)
        {
            broadcastCount++;
            Record("Broadcast exact source", message.traceId, message.sourceLabel);
        }

        private void OnAnyUntargeted(IUntargetedMessage message)
        {
            globalAcceptAllCount++;
        }

        private void OnAnyTargeted(InstanceId target, ITargetedMessage message)
        {
            globalAcceptAllCount++;
        }

        private void OnAnyBroadcast(InstanceId source, IBroadcastMessage message)
        {
            globalAcceptAllCount++;
        }

        private void Record(string route, string traceId, string payload)
        {
            lastRoute = route;
            lastTraceId = traceId;
            lastPayload = payload;

            if (logMessages)
            {
                Debug.Log($"[{listenerLabel}] {lastRoute} {lastTraceId}: {lastPayload}", this);
            }
        }
    }
}
