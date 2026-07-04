namespace WallstopStudios.DxMessagingSamples.DiagnosticsToolingExerciser
{
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

        private bool toolingRegistrationsEnsured;

        public string ListenerLabel => listenerLabel;

        public int UntargetedCount => untargetedCount;

        public int TargetedCount => targetedCount;

        public int BroadcastCount => broadcastCount;

        public int GlobalAcceptAllCount => globalAcceptAllCount;

        protected override bool RegisterForStringMessages => false;

        protected override void Awake()
        {
            base.Awake();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
        }

        private void Start()
        {
            EnsureToolingRegistrations();
        }

        public void EnsureToolingRegistrations()
        {
            if (Token == null)
            {
                return;
            }

            if (!toolingRegistrationsEnsured)
            {
                RegisterMessageHandlers();
                toolingRegistrationsEnsured = true;
            }

            Token.Enable();
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
