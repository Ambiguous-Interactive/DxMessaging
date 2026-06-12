namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using System;
    using DxMessaging.Unity;
    using Messages;

    /// <summary>
    /// Test listener whose <see cref="MessageAwareComponent.ReregisterOnEnableAfterRelease"/>
    /// override is driven by a public field so fixtures can exercise both opt-in and opt-out
    /// behavior with one component type.
    /// </summary>
    public sealed class ReregisteringMessageAwareComponent : MessageAwareComponent
    {
        public Action untargetedHandler;

        public bool reregisterOnEnableAfterRelease = true;

        public bool tieRegistrationToEnableStatus = true;

        /// <summary>
        /// Number of times <see cref="RegisterMessageHandlers"/> ran; lets tests prove the
        /// replay happens exactly once per release instead of once per enable cycle.
        /// </summary>
        public int registerInvocationCount;

        protected override bool RegisterForStringMessages => false;

        protected override bool ReregisterOnEnableAfterRelease => reregisterOnEnableAfterRelease;

        protected override bool MessageRegistrationTiedToEnableStatus =>
            tieRegistrationToEnableStatus;

        protected override void RegisterMessageHandlers()
        {
            ++registerInvocationCount;
            _ = _messageRegistrationToken.RegisterUntargeted<SimpleUntargetedMessage>(
                HandleSimpleUntargetedMessage
            );
        }

        private void HandleSimpleUntargetedMessage(ref SimpleUntargetedMessage message)
        {
            untargetedHandler?.Invoke();
        }
    }
}
