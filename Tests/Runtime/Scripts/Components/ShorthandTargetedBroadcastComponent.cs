namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;

    public sealed class ShorthandTargetedBroadcastComponent : MessageAwareComponent
    {
        public int gameObjectTargetedCount;
        public int componentTargetedCount;
        public int targetedWithoutTargetingCount;

        public int gameObjectBroadcastCount;
        public int componentBroadcastCount;
        public int broadcastWithoutSourceCount;

        protected override void RegisterMessageHandlers()
        {
            base.RegisterMessageHandlers();

            _ = Token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                gameObject,
                _ => gameObjectTargetedCount++
            );
            _ = Token.RegisterComponentTargeted<SimpleTargetedMessage>(
                this,
                _ => componentTargetedCount++
            );
            _ = Token.RegisterTargetedWithoutTargeting(
                (in InstanceId _, in SimpleTargetedMessage _) => targetedWithoutTargetingCount++
            );

            _ = Token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                gameObject,
                _ => gameObjectBroadcastCount++
            );
            _ = Token.RegisterComponentBroadcast<SimpleBroadcastMessage>(
                this,
                _ => componentBroadcastCount++
            );
            _ = Token.RegisterBroadcastWithoutSource(
                (in InstanceId _, in SimpleBroadcastMessage _) => broadcastWithoutSourceCount++
            );
        }
    }
}
