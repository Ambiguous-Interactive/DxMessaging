#pragma warning disable RCS1242 // Fast handlers intentionally observe mutable messages by readonly reference.
namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Unity;
    using Messages;

    public sealed class SimpleMessageAwareComponent : MessageAwareComponent
    {
        public bool SlowComplexTargetingEnabled
        {
            get => _slowComplexTargetingEnabled;
            set
            {
                _slowComplexTargetingEnabled = value;
                ToggleTargetedRegistration();
            }
        }
        public bool FastComplexTargetingEnabled
        {
            get => _fastComplexTargetingEnabled;
            set
            {
                _fastComplexTargetingEnabled = value;
                ToggleTargetedRegistration();
            }
        }

        public Action untargetedHandler;
        public Action targetedHandler;
        public Action slowTargetedHandler;
        public Action targetedWithoutTargetingHandler;
        public Action slowComplexTargetedHandler;
        public Action complexTargetedHandler;
        public Action broadcastHandler;
        public Action broadcastWithoutSourceHandler;
        public Action componentTargetedHandler;
        public Action complexComponentTargetedHandler;
        public Action componentBroadcastHandler;
        public Action reflexiveNoArgumentHandler;
        public Action reflexiveIgnoredArgumentHandler;
        public Action reflexiveObjectArgumentHandler;
        public Action reflexiveOneArgumentHandler;
        public Action reflexiveTwoArgumentHandler;
        public Action reflexiveThreeArgumentHandler;

        private bool _slowComplexTargetingEnabled = true;
        private bool _fastComplexTargetingEnabled = true;

        private MessageRegistrationHandle? _slowComplexTargetingHandle;
        private MessageRegistrationHandle? _fastComplexTargetingHandle;

        protected override bool RegisterForStringMessages => false;

        protected override void RegisterMessageHandlers()
        {
            _ = _messageRegistrationToken.RegisterUntargeted<SimpleUntargetedMessage>(
                HandleSimpleUntargetedMessage
            );
            _ = _messageRegistrationToken.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                gameObject,
                HandleSimpleTargetedMessage
            );
            _ = _messageRegistrationToken.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                gameObject,
                HandleSlowSimpleTargetedMessage
            );
            _ = _messageRegistrationToken.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                HandleSimpleTargetedWithoutTargetingMessage
            );
            _fastComplexTargetingHandle =
                _messageRegistrationToken.RegisterGameObjectTargeted<ComplexTargetedMessage>(
                    gameObject,
                    HandleComplexTargetedMessage
                );
            _slowComplexTargetingHandle =
                _messageRegistrationToken.RegisterGameObjectTargeted<ComplexTargetedMessage>(
                    gameObject,
                    HandleSlowComplexTargetedMessage
                );
            _ = _messageRegistrationToken.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                gameObject,
                HandleSimpleBroadcastMessage
            );
            _ = _messageRegistrationToken.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                HandleSimpleBroadcastWithoutSourceMessage
            );
            _ = _messageRegistrationToken.RegisterComponentTargeted<SimpleTargetedMessage>(
                this,
                HandleSimpleComponentTargetedMessage
            );
            _ = _messageRegistrationToken.RegisterComponentBroadcast<SimpleBroadcastMessage>(
                this,
                HandleSimpleComponentBroadcastMessage
            );
            _ = _messageRegistrationToken.RegisterComponentTargeted<ComplexTargetedMessage>(
                this,
                HandleComplexComponentTargetedMessage
            );
        }

        private void ToggleTargetedRegistration()
        {
            if (SlowComplexTargetingEnabled)
            {
                _slowComplexTargetingHandle ??=
                    _messageRegistrationToken.RegisterGameObjectTargeted<ComplexTargetedMessage>(
                        gameObject,
                        HandleSlowComplexTargetedMessage
                    );
            }
            else if (_slowComplexTargetingHandle != null)
            {
                _messageRegistrationToken.RemoveRegistration(_slowComplexTargetingHandle.Value);
                _slowComplexTargetingHandle = null;
            }

            if (FastComplexTargetingEnabled)
            {
                _fastComplexTargetingHandle ??=
                    _messageRegistrationToken.RegisterGameObjectTargeted<ComplexTargetedMessage>(
                        gameObject,
                        HandleComplexTargetedMessage
                    );
            }
            else if (_fastComplexTargetingHandle != null)
            {
                _messageRegistrationToken.RemoveRegistration(_fastComplexTargetingHandle.Value);
                _fastComplexTargetingHandle = null;
            }
        }

        public void HandleReflexiveMessageNoArguments()
        {
            reflexiveNoArgumentHandler?.Invoke();
        }

        public void HandleReflexiveMessageIgnoringArgument()
        {
            reflexiveIgnoredArgumentHandler?.Invoke();
        }

        public void HandleReflexiveMessageObjectArgument(object value)
        {
            reflexiveObjectArgumentHandler?.Invoke();
        }

        public void HandleReflexiveMessageOneArgument(int value)
        {
            reflexiveOneArgumentHandler?.Invoke();
        }

        public void HandleReflexiveMessageTwoArguments(int a, int b)
        {
            reflexiveTwoArgumentHandler?.Invoke();
        }

        public void HandleReflexiveMessageThreeArguments(int a, int b, int c)
        {
            reflexiveThreeArgumentHandler?.Invoke();
        }

        public void HandleSlowComplexTargetedMessage(ComplexTargetedMessage message)
        {
            slowComplexTargetedHandler?.Invoke();
        }

        public void HandleComplexTargetedMessage(in ComplexTargetedMessage message)
        {
            complexTargetedHandler?.Invoke();
        }

        public void HandleSlowSimpleTargetedMessage(SimpleTargetedMessage message)
        {
            slowTargetedHandler?.Invoke();
        }

        public void HandleSimpleUntargetedMessage(in SimpleUntargetedMessage message)
        {
            untargetedHandler?.Invoke();
        }

        public void HandleSimpleTargetedMessage(in SimpleTargetedMessage message)
        {
            targetedHandler?.Invoke();
        }

        public void HandleSimpleTargetedWithoutTargetingMessage(
            in InstanceId target,
            in SimpleTargetedMessage message
        )
        {
            targetedWithoutTargetingHandler?.Invoke();
        }

        public void HandleSimpleBroadcastMessage(in SimpleBroadcastMessage message)
        {
            broadcastHandler?.Invoke();
        }

        public void HandleSimpleBroadcastWithoutSourceMessage(
            in InstanceId source,
            in SimpleBroadcastMessage message
        )
        {
            broadcastWithoutSourceHandler?.Invoke();
        }

        public void HandleSimpleComponentTargetedMessage(in SimpleTargetedMessage message)
        {
            componentTargetedHandler?.Invoke();
        }

        public void HandleComplexComponentTargetedMessage(in ComplexTargetedMessage message)
        {
            complexComponentTargetedHandler?.Invoke();
        }

        public void HandleSimpleComponentBroadcastMessage(in SimpleBroadcastMessage message)
        {
            componentBroadcastHandler?.Invoke();
        }
    }
}
#pragma warning restore RCS1242
