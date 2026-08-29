namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using DxMessaging.Core;
    using DxMessaging.Core.Messages;
    using DxMessaging.Unity;

    public sealed class StringMessageAwareComponent : MessageAwareComponent
    {
        public int gameObjectTargetedCount;
        public int componentTargetedCount;
        public int untargetedGlobalCount;
        public int gameObjectBroadcastCount;
        public int componentBroadcastCount;
        public int broadcastWithoutSourceCount;
        public int targetedWithoutTargetingCount;

        protected override void RegisterMessageHandlers()
        {
            base.RegisterMessageHandlers();

            _ = Token.RegisterGameObjectTargeted<StringMessage>(
                gameObject,
                HandleStringGameObjectTargeted
            );
            _ = Token.RegisterComponentTargeted<StringMessage>(this, HandleStringComponentTargeted);

            _ = Token.RegisterTargetedWithoutTargeting<StringMessage>(HandleAnyStringTargeted);

            _ = Token.RegisterGameObjectBroadcast<SourcedStringMessage>(
                gameObject,
                HandleSourcedStringGameObjectBroadcast
            );
            _ = Token.RegisterComponentBroadcast<SourcedStringMessage>(
                this,
                HandleSourcedStringComponentBroadcast
            );
            _ = Token.RegisterBroadcastWithoutSource<SourcedStringMessage>(HandleAnySourcedString);
        }

        private void HandleStringGameObjectTargeted(in StringMessage message)
        {
            gameObjectTargetedCount++;
        }

        private void HandleStringComponentTargeted(in StringMessage message)
        {
            componentTargetedCount++;
        }

        private void HandleAnyStringTargeted(in InstanceId target, in StringMessage message)
        {
            targetedWithoutTargetingCount++;
        }

        protected override void HandleGlobalStringMessage(in GlobalStringMessage message)
        {
            untargetedGlobalCount++;
        }

        private void HandleSourcedStringGameObjectBroadcast(in SourcedStringMessage message)
        {
            gameObjectBroadcastCount++;
        }

        private void HandleSourcedStringComponentBroadcast(in SourcedStringMessage message)
        {
            componentBroadcastCount++;
        }

        private void HandleAnySourcedString(in InstanceId source, in SourcedStringMessage message)
        {
            broadcastWithoutSourceCount++;
        }
    }
}
