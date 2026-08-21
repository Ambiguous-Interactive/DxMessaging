#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    public sealed class DxMessagingStaticStateTests
    {
        [Test]
        public void ResetRestoresDefaultStaticState()
        {
            DxMessagingStaticState.Reset();
            IMessageBus baselineBus = MessageHandler.MessageBus;
            MessageRegistrationHandle baselineHandle =
                MessageRegistrationHandle.CreateMessageRegistrationHandle();

            MessageRegistrationHandle lastPreResetHandle =
                MessageRegistrationHandle.CreateMessageRegistrationHandle();
            MessagingDebug.enabled = true;
            MessagingDebug.LogFunction = (logLevel, message) => { };

            IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.All;
            IMessageBus.GlobalDiagnosticsStackTraces = true;
            IMessageBus.GlobalMessageBufferSize = 128;
            IMessageBus.GlobalSequentialIndex = 7;

            Type builderType = typeof(MessageRegistrationBuilder);
            FieldInfo syntheticOwnerField = builderType.GetField(
                "_syntheticOwnerCounter",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(syntheticOwnerField);
            syntheticOwnerField.SetValue(null, 3);

            MessageHandler.SetGlobalMessageBus(new MessageBus());

            DxMessagingStaticState.Reset();

            Assert.IsFalse(MessagingDebug.enabled);
            Assert.IsNull(MessagingDebug.LogFunction);
            Assert.AreEqual(DiagnosticsTarget.Off, IMessageBus.GlobalDiagnosticsTargets);
            Assert.IsFalse(IMessageBus.GlobalDiagnosticsStackTraces);
            Assert.AreEqual(100, IMessageBus.GlobalMessageBufferSize);
            Assert.AreEqual(-1, IMessageBus.GlobalSequentialIndex);

            MessageRegistrationHandle resetHandle =
                MessageRegistrationHandle.CreateMessageRegistrationHandle();
            Assert.Greater(
                resetHandle,
                lastPreResetHandle,
                "Reset must not reuse registration identities that stale handles may still carry. baseline={0}, lastPreReset={1}, postReset={2}",
                baselineHandle,
                lastPreResetHandle,
                resetHandle
            );

            object syntheticOwnerValue = syntheticOwnerField.GetValue(null);
            Assert.AreEqual(0, (int)syntheticOwnerValue);

            Assert.AreSame(MessageHandler.InitialGlobalMessageBus, MessageHandler.MessageBus);
            Assert.AreSame(baselineBus, MessageHandler.MessageBus);

            DxMessagingStaticState.Reset();
        }

        [Test]
        public void MessageTypeIdIsStableAfterReset()
        {
            MessageCache<object> cache = new MessageCache<object>();
            cache.GetOrAdd<StabilityTestMessage>();

            int idBeforeReset = MessageHelperIndexer<StabilityTestMessage>.SequentialId;
            Assert.GreaterOrEqual(
                idBeforeReset,
                0,
                "Message type should have a valid sequential ID after registration"
            );

            DxMessagingStaticState.Reset();

            int idAfterReset = MessageHelperIndexer<StabilityTestMessage>.SequentialId;
            Assert.AreEqual(
                idBeforeReset,
                idAfterReset,
                "Message type ID should be preserved after Reset()"
            );
        }

        [Test]
        public void TotalMessagesPreservedAfterReset()
        {
            MessageCache<object> cache = new MessageCache<object>();
            cache.GetOrAdd<TotalMessagesTestMessage>();

            int totalBeforeReset = MessageHelperIndexer.TotalMessages;
            Assert.Greater(
                totalBeforeReset,
                0,
                "TotalMessages should be greater than zero after registering a message type"
            );

            DxMessagingStaticState.Reset();

            int totalAfterReset = MessageHelperIndexer.TotalMessages;
            Assert.AreEqual(
                totalBeforeReset,
                totalAfterReset,
                "TotalMessages should be preserved after Reset()"
            );
        }

        private struct StabilityTestMessage : IUntargetedMessage { }

        private struct TotalMessagesTestMessage : IUntargetedMessage { }
    }
}
#endif
