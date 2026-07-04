namespace WallstopStudios.DxMessagingSamples.DiagnosticsToolingExerciser
{
    using DxMessaging.Core.Messages;

    public readonly struct ToolingPulse : IUntargetedMessage<ToolingPulse>
    {
        public readonly string traceId;
        public readonly string channel;
        public readonly int sequence;

        public ToolingPulse(string traceId, string channel, int sequence)
        {
            this.traceId = traceId;
            this.channel = channel;
            this.sequence = sequence;
        }
    }

    public readonly struct ToolingCommand : ITargetedMessage<ToolingCommand>
    {
        public readonly string traceId;
        public readonly string command;
        public readonly int sequence;

        public ToolingCommand(string traceId, string command, int sequence)
        {
            this.traceId = traceId;
            this.command = command;
            this.sequence = sequence;
        }
    }

    public readonly struct ToolingSignal : IBroadcastMessage<ToolingSignal>
    {
        public readonly string traceId;
        public readonly string sourceLabel;
        public readonly int sequence;

        public ToolingSignal(string traceId, string sourceLabel, int sequence)
        {
            this.traceId = traceId;
            this.sourceLabel = sourceLabel;
            this.sequence = sequence;
        }
    }
}
