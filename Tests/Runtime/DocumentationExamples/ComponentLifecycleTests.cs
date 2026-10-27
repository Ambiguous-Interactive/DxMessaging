#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.DocumentationExamples
{
    using System;
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    [Category("DocumentationExamples")]
    public sealed class ComponentLifecycleTests
    {
        private MessageBus _bus;
        private IDisposable _busScope;
        private GameObject _host;

        [SetUp]
        public void SetUp()
        {
            _bus = new MessageBus();
            _busScope = MessageHandler.OverrideGlobalMessageBus(_bus);
        }

        [Test]
        public void ReceiverTracksEnabledState()
        {
            _host = new GameObject("TestPingReceiver");
            TestPingReceiver receiver = _host.AddComponent<TestPingReceiver>();
            TestPing message = new TestPing();

            message.Emit(_bus);
            Assert.That(receiver.Received, Is.EqualTo(1));

            receiver.enabled = false;
            message.Emit(_bus);
            Assert.That(receiver.Received, Is.EqualTo(1));

            receiver.enabled = true;
            message.Emit(_bus);
            Assert.That(receiver.Received, Is.EqualTo(2));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try
            {
                if (_host != null)
                {
                    UnityEngine.Object.Destroy(_host);
                }
                yield return null;
                _host = null;

                Assert.That(_bus.RegisteredUntargeted, Is.Zero);
                Assert.That(_bus.RegisteredTargeted, Is.Zero);
                Assert.That(_bus.RegisteredBroadcast, Is.Zero);
                Assert.That(_bus.RegisteredInterceptors, Is.Zero);
                Assert.That(_bus.RegisteredPostProcessors, Is.Zero);
                Assert.That(_bus.RegisteredGlobalAcceptAll, Is.Zero);
            }
            finally
            {
                _busScope.Dispose();
            }
        }
    }
}
#endif
