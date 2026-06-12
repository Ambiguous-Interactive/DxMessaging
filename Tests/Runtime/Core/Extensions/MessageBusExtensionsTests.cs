namespace DxMessaging.Tests.Runtime.Core.Extensions
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using MessageBus = DxMessaging.Core.MessageBus.MessageBus;

    [TestFixture]
    public sealed class MessageBusExtensionsTests
    {
        private GlobalBusScope _globalBusScope;

        [SetUp]
        public void SetUp()
        {
            _globalBusScope = GlobalBusScope.CaptureAndReset();
        }

        [TearDown]
        public void TearDown()
        {
            _globalBusScope?.Dispose();
            _globalBusScope = null;
        }

        [Test]
        public void EmitUntargetedClassMessageUsesBus()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(10), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int count = 0;
            _ = token.RegisterUntargeted((ref ClassUntargetedMessage _) => count++);
            token.Enable();

            ClassUntargetedMessage message = new ClassUntargetedMessage();
            bus.EmitUntargeted(message);

            Assert.AreEqual(1, count);
            token.Disable();
        }

        [Test]
        public void EmitUntargetedStructMessageUsesBus()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(20), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int count = 0;
            _ = token.RegisterUntargeted((ref StructUntargetedMessage _) => count++);
            token.Enable();

            StructUntargetedMessage message = new StructUntargetedMessage(1);
            bus.EmitUntargeted(ref message);

            Assert.AreEqual(1, count);
            token.Disable();
        }

        [Test]
        public void EmitUntargetedStructMessageHonorsInterceptorsAndPostProcessors()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(21), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            StructInterceptedMessage intercepted = default;
            int postProcessCount = 0;

            _ = bus.RegisterUntargetedInterceptor(
                (ref StructInterceptedMessage msg) =>
                {
                    msg.Value += 10;
                    return true;
                }
            );

            _ = token.RegisterUntargeted((ref StructInterceptedMessage msg) => intercepted = msg);

            _ = token.RegisterUntargetedPostProcessor(
                (ref StructInterceptedMessage _) => postProcessCount++
            );

            token.Enable();

            StructInterceptedMessage message = new StructInterceptedMessage(5);
            bus.EmitUntargeted(ref message);

            Assert.AreEqual(15, intercepted.Value);
            Assert.AreEqual(1, postProcessCount);

            token.Disable();
        }

        [Test]
        public void EmitUntargetedRandomizedMatchesMessageExtensions()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(25), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int busSum = 0;

            _ = token.RegisterUntargeted((ref StructUntargetedMessage msg) => busSum += msg.Value);

            token.Enable();

            const int iterations = 256;
            int[] values = new int[iterations];
            Random random = new Random(1234);

            for (int i = 0; i < iterations; i++)
            {
                int value = random.Next(-1_000, 1_000);
                values[i] = value;
                StructUntargetedMessage message = new StructUntargetedMessage(value);
                bus.EmitUntargeted(ref message);
            }

            token.Disable();

            MessageHandler handler2 = new MessageHandler(new InstanceId(26), bus) { active = true };
            MessageRegistrationToken token2 = MessageRegistrationToken.Create(handler2, bus);
            int messageSum = 0;

            _ = token2.RegisterUntargeted(
                (ref StructUntargetedMessage msg) => messageSum += msg.Value
            );

            token2.Enable();

            for (int i = 0; i < iterations; i++)
            {
                StructUntargetedMessage message = new StructUntargetedMessage(values[i]);
                message.EmitUntargeted(bus);
            }

            token2.Disable();

            Assert.AreEqual(busSum, messageSum);
        }

        [Test]
        public void EmitTargetedStructMessageUsesBus()
        {
            MessageBus bus = new MessageBus();
            InstanceId target = new InstanceId(42);

            MessageHandler handler = new MessageHandler(new InstanceId(30), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int count = 0;
            _ = token.RegisterTargeted(target, (ref StructTargetedMessage _) => count++);
            token.Enable();

            StructTargetedMessage message = new StructTargetedMessage(5);
            bus.EmitTargeted(target, ref message);

            Assert.AreEqual(1, count);
            token.Disable();
        }

        [Test]
        public void EmitBroadcastStructMessageUsesBus()
        {
            MessageBus bus = new MessageBus();
            InstanceId source = new InstanceId(99);

            MessageHandler handler = new MessageHandler(new InstanceId(40), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int count = 0;
            _ = token.RegisterBroadcast(source, (ref StructBroadcastMessage _) => count++);
            token.Enable();

            StructBroadcastMessage message = new StructBroadcastMessage(7);
            bus.EmitBroadcast(source, ref message);

            Assert.AreEqual(1, count);
            token.Disable();
        }

        [Test]
        public void EmitStringHelpersUseBus()
        {
            MessageBus bus = new MessageBus();
            InstanceId target = new InstanceId(11);
            InstanceId source = new InstanceId(12);

            MessageHandler handler = new MessageHandler(new InstanceId(50), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            string targeted = null;
            string broadcast = null;
            string untargeted = null;

            _ = token.RegisterTargeted(target, (ref StringMessage m) => targeted = m.message);

            _ = token.RegisterBroadcast(
                source,
                (ref SourcedStringMessage m) => broadcast = m.message
            );

            _ = token.RegisterUntargeted((ref GlobalStringMessage m) => untargeted = m.message);

            token.Enable();

            bus.EmitAt(target, "target");
            bus.EmitFrom(source, "broadcast");
            bus.Emit("untargeted");

            Assert.AreEqual("target", targeted);
            Assert.AreEqual("broadcast", broadcast);
            Assert.AreEqual("untargeted", untargeted);

            token.Disable();
        }

        private sealed class ClassUntargetedMessage : IUntargetedMessage { }

        private readonly struct StructUntargetedMessage : IUntargetedMessage
        {
            internal StructUntargetedMessage(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }

        private struct StructInterceptedMessage : IUntargetedMessage
        {
            internal StructInterceptedMessage(int value)
            {
                Value = value;
            }

            internal int Value;
        }

        private readonly struct StructTargetedMessage : ITargetedMessage
        {
            internal StructTargetedMessage(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }

        private readonly struct StructBroadcastMessage : IBroadcastMessage
        {
            internal StructBroadcastMessage(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }
    }
}
