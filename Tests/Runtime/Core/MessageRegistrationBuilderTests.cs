namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    public sealed class MessageRegistrationBuilderTests
    {
        private sealed class PassthroughMessageBusProvider : IMessageBusProvider
        {
            private readonly IMessageBus _messageBus;

            public PassthroughMessageBusProvider(IMessageBus messageBus)
            {
                _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            }

            public IMessageBus Resolve()
            {
                return _messageBus;
            }
        }

        private MessageRegistrationBuilder _builder;
        private IMessageBus _defaultBus;

        [SetUp]
        public void SetUp()
        {
            _defaultBus = new MessageBus();
            IMessageBusProvider provider = new PassthroughMessageBusProvider(_defaultBus);
            _builder = new MessageRegistrationBuilder(provider);
        }

        [TearDown]
        public void TearDown()
        {
            _builder = null;
            _defaultBus = null;
        }

        [Test]
        public void BuildUsesResolvedBusWhenNoOverrideProvided()
        {
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions();
            using MessageRegistrationLease lease = _builder.Build(options);

            Assert.AreSame(_defaultBus, lease.MessageBus);
            Assert.IsNotNull(lease.Token);
            Assert.IsFalse(lease.Token.Enabled);
        }

        [Test]
        public void ActivateOnBuildEnablesTokenAndLifecycle()
        {
            bool buildInvoked = false;
            bool activateInvoked = false;
            bool deactivateInvoked = false;
            bool disposeInvoked = false;
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
            {
                ActivateOnBuild = true,
                Configure = token =>
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(OnSimpleMessage);
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    token =>
                    {
                        buildInvoked = true;
                    },
                    token =>
                    {
                        activateInvoked = true;
                    },
                    token =>
                    {
                        deactivateInvoked = true;
                    },
                    token =>
                    {
                        disposeInvoked = true;
                    }
                ),
            };

            using (MessageRegistrationLease lease = _builder.Build(options))
            {
                Assert.IsTrue(buildInvoked);
                Assert.IsTrue(lease.Token.Enabled);
                Assert.IsTrue(lease.IsActive);
                Assert.IsTrue(activateInvoked);

                lease.Deactivate();
                Assert.IsFalse(lease.IsActive);
                Assert.IsTrue(deactivateInvoked);
            }

            Assert.IsTrue(disposeInvoked);
        }

        [Test]
        public void PreferredMessageBusOverridesProvider()
        {
            MessageBus preferredBus = new MessageBus();
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
            {
                PreferredMessageBus = preferredBus,
            };

            using MessageRegistrationLease lease = _builder.Build(options);
            Assert.AreSame(preferredBus, lease.MessageBus);
        }

        [Test]
        public void MessageBusProviderOptionOverridesBuilderDefault()
        {
            MessageBus providerBus = new MessageBus();
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
            {
                MessageBusProvider = new PassthroughMessageBusProvider(providerBus),
            };

            using MessageRegistrationLease lease = _builder.Build(options);
            Assert.AreSame(providerBus, lease.MessageBus);
        }

        [Test]
        public void SyntheticOwnerGeneratedWhenOwnerMissing()
        {
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions();
            using MessageRegistrationLease lease = _builder.Build(options);
            Assert.AreNotEqual(InstanceId.EmptyId, lease.Owner);
        }

        [Test]
        public void HandlerStartsInactiveWhenConfigured()
        {
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
            {
                HandlerStartsActive = false,
            };

            using MessageRegistrationLease lease = _builder.Build(options);

            Assert.IsFalse(lease.Handler.active);
            lease.Activate();
            Assert.IsTrue(lease.IsActive);
            Assert.IsTrue(lease.Handler.active);
        }

        [Test]
        public void DiagnosticsModeRespectedWhenEnabled()
        {
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
            {
                EnableDiagnostics = true,
                Configure = token =>
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(OnSimpleMessage);
                },
            };

            using MessageRegistrationLease lease = _builder.Build(options);
            Assert.IsTrue(
                lease.Token.DiagnosticMode,
                "Diagnostics flag should propagate to the token."
            );
        }

        [Test]
        public void DisposingActiveLeaseInvokesDeactivateLifecycle()
        {
            bool deactivateInvoked = false;
            bool disposeInvoked = false;

            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
            {
                ActivateOnBuild = true,
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    null,
                    token => deactivateInvoked = true,
                    token => disposeInvoked = true
                ),
            };

            using (MessageRegistrationLease lease = _builder.Build(options))
            {
                // No-op
            }

            Assert.IsTrue(
                deactivateInvoked,
                "Disposing an active lease should trigger OnDeactivate before tear-down."
            );
            Assert.IsTrue(disposeInvoked, "Disposing a lease should trigger OnDispose callbacks.");
        }

        private static void OnSimpleMessage(in SimpleUntargetedMessage message) { }
    }
}
