#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.DocumentationExamples
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    public readonly struct TestPing : IUntargetedMessage<TestPing> { }

    [Category("DocumentationExamples")]
    public sealed class PlainMessageTests
    {
        [Test]
        public void LocalSubscriptionDeliversAndCleansUp()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(1), bus) { active = true };
            int received = 0;
            TestPing message = new TestPing();

            using (MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus))
            {
                _ = token.RegisterUntargeted<TestPing>((in TestPing ping) => received++);
                token.Enable();
                message.Emit(bus);
                Assert.That(received, Is.EqualTo(1));
            }

            Assert.That(bus.RegisteredUntargeted, Is.Zero);
            message.Emit(bus);
            Assert.That(received, Is.EqualTo(1), "Disposal must remove the subscription.");
        }
    }
}
#endif
