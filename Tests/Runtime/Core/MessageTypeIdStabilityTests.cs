#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using UnityEngine.TestTools;

    public sealed class MessageTypeIdStabilityTests
    {
        private MessageBus _messageBus;
        private MessageHandler _handler;
        private InstanceId _instanceId;
        private MessageRegistrationToken _token;

        [SetUp]
        public void Setup()
        {
            _instanceId = new InstanceId(42);
            _messageBus = new MessageBus();
            _handler = new MessageHandler(_instanceId, _messageBus) { active = true };
            _token = MessageRegistrationToken.Create(_handler, _messageBus);
            _token.Enable();
        }

        [TearDown]
        public void Cleanup()
        {
            _token?.Dispose();
            DxMessagingStaticState.Reset();
        }

        [UnityTest]
        public IEnumerator MessageRegisteredBeforeResetStillRoutesAfterReset()
        {
            MessageCache<object> cache = new MessageCache<object>();
            cache.GetOrAdd<RoutingTestMessage>();

            int idBeforeReset = MessageHelperIndexer<RoutingTestMessage>.SequentialId;
            Assert.GreaterOrEqual(
                idBeforeReset,
                0,
                "Message type should have a valid sequential ID after registration"
            );

            int receivedCount = 0;
            MessageRegistrationHandle handle = _token.RegisterUntargeted<RoutingTestMessage>(
                (ref RoutingTestMessage msg) =>
                {
                    ++receivedCount;
                }
            );

            RoutingTestMessage message = new RoutingTestMessage();
            _messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, receivedCount, "Message should be received before reset");

            DxMessagingStaticState.Reset();

            int idAfterReset = MessageHelperIndexer<RoutingTestMessage>.SequentialId;
            Assert.AreEqual(
                idBeforeReset,
                idAfterReset,
                "Message type ID should be preserved after Reset()"
            );

            _messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(2, receivedCount, "Message should still route correctly after reset");

            yield return null;
            _token.RemoveRegistration(handle);
        }

        [UnityTest]
        public IEnumerator HandlerRegisteredAfterResetReceivesMessages()
        {
            DxMessagingStaticState.Reset();

            int receivedCount = 0;
            MessageRegistrationHandle handle = _token.RegisterUntargeted<PostResetRoutingMessage>(
                (ref PostResetRoutingMessage msg) =>
                {
                    ++receivedCount;
                }
            );

            PostResetRoutingMessage message = new PostResetRoutingMessage();
            _messageBus.UntargetedBroadcast(ref message);

            Assert.AreEqual(
                1,
                receivedCount,
                "Handler registered after reset should receive messages"
            );

            yield return null;
            _token.RemoveRegistration(handle);
        }

        [Test]
        public void MultipleMessageTypesRemainDistinctAfterReset()
        {
            MessageCache<object> cache = new MessageCache<object>();
            cache.GetOrAdd<DistinctMessageA>();
            cache.GetOrAdd<DistinctMessageB>();
            cache.GetOrAdd<DistinctMessageC>();

            int idA = MessageHelperIndexer<DistinctMessageA>.SequentialId;
            int idB = MessageHelperIndexer<DistinctMessageB>.SequentialId;
            int idC = MessageHelperIndexer<DistinctMessageC>.SequentialId;

            Assert.GreaterOrEqual(idA, 0, "DistinctMessageA should have a valid ID");
            Assert.GreaterOrEqual(idB, 0, "DistinctMessageB should have a valid ID");
            Assert.GreaterOrEqual(idC, 0, "DistinctMessageC should have a valid ID");

            Assert.AreNotEqual(
                idA,
                idB,
                "DistinctMessageA and DistinctMessageB should have different IDs"
            );
            Assert.AreNotEqual(
                idB,
                idC,
                "DistinctMessageB and DistinctMessageC should have different IDs"
            );
            Assert.AreNotEqual(
                idA,
                idC,
                "DistinctMessageA and DistinctMessageC should have different IDs"
            );

            DxMessagingStaticState.Reset();

            int idAAfterReset = MessageHelperIndexer<DistinctMessageA>.SequentialId;
            int idBAfterReset = MessageHelperIndexer<DistinctMessageB>.SequentialId;
            int idCAfterReset = MessageHelperIndexer<DistinctMessageC>.SequentialId;

            Assert.AreEqual(
                idA,
                idAAfterReset,
                "DistinctMessageA ID should be preserved after reset"
            );
            Assert.AreEqual(
                idB,
                idBAfterReset,
                "DistinctMessageB ID should be preserved after reset"
            );
            Assert.AreEqual(
                idC,
                idCAfterReset,
                "DistinctMessageC ID should be preserved after reset"
            );

            Assert.AreNotEqual(
                idAAfterReset,
                idBAfterReset,
                "Message types should remain distinct after reset"
            );
            Assert.AreNotEqual(
                idBAfterReset,
                idCAfterReset,
                "Message types should remain distinct after reset"
            );
            Assert.AreNotEqual(
                idAAfterReset,
                idCAfterReset,
                "Message types should remain distinct after reset"
            );
        }

        [Test]
        public void NewMessageTypesRegisteredAfterResetGetCorrectIds()
        {
            MessageCache<object> cache = new MessageCache<object>();
            cache.GetOrAdd<PreResetMessage>();

            int preResetId = MessageHelperIndexer<PreResetMessage>.SequentialId;
            int totalBeforeReset = MessageHelperIndexer.TotalMessages;

            Assert.GreaterOrEqual(preResetId, 0, "PreResetMessage should have a valid ID");
            Assert.Greater(
                totalBeforeReset,
                0,
                "TotalMessages should be greater than zero after registration"
            );

            DxMessagingStaticState.Reset();

            // Capture the running id count immediately around the new-type
            // registration so the delta is attributable to exactly this
            // GetOrAdd call. MessageHelperIndexer assigns each type id once per
            // process domain and NEVER resets it (see its xmldoc: this is the
            // routing-stability contract that makes "Enter Play Mode with Domain
            // Reload disabled" safe). So registering PostResetMessage either
            // assigns the next sequential id -- first time in this domain, the
            // count grows by exactly one and the new id equals the previous
            // count -- or returns the id assigned on an earlier run when the
            // domain persisted across play-mode entries, leaving the count
            // steady. A strict "the global total grew" assertion would falsely
            // fail in that second case (it did, once enter-play-mode domain
            // reload was disabled for the test runner), so assert the invariants
            // that hold in BOTH: a valid, distinct, counted id and a
            // never-shrinking total.
            int totalBeforeNewType = MessageHelperIndexer.TotalMessages;
            cache.GetOrAdd<PostResetMessage>();

            int postResetId = MessageHelperIndexer<PostResetMessage>.SequentialId;
            int totalAfterReset = MessageHelperIndexer.TotalMessages;

            Assert.GreaterOrEqual(postResetId, 0, "PostResetMessage should have a valid ID");
            Assert.AreNotEqual(
                preResetId,
                postResetId,
                "New message type should get a different ID than existing types"
            );

            int registrationDelta = totalAfterReset - totalBeforeNewType;
            Assert.That(
                registrationDelta,
                Is.EqualTo(0).Or.EqualTo(1),
                "Registering one message type must change TotalMessages by zero "
                    + "(already assigned in a persistent domain) or one (freshly assigned)"
            );
            if (registrationDelta == 1)
            {
                Assert.AreEqual(
                    totalBeforeNewType,
                    postResetId,
                    "A freshly assigned message type must take the next sequential id"
                );
            }
            Assert.Greater(
                totalAfterReset,
                postResetId,
                "TotalMessages must account for the new type id (ids are a 0-based "
                    + "index below the running count)"
            );
            Assert.GreaterOrEqual(
                totalAfterReset,
                totalBeforeReset,
                "TotalMessages must never shrink (ids are assigned once and preserved)"
            );

            int preResetIdAfterNewRegistration = MessageHelperIndexer<PreResetMessage>.SequentialId;
            Assert.AreEqual(
                preResetId,
                preResetIdAfterNewRegistration,
                "Pre-existing message type ID should not change when new types are registered"
            );
        }

        [Test]
        public void MultipleResetCyclesPreserveIds()
        {
            MessageCache<object> cache = new MessageCache<object>();
            cache.GetOrAdd<MultiResetMessage>();

            int originalId = MessageHelperIndexer<MultiResetMessage>.SequentialId;
            int originalTotal = MessageHelperIndexer.TotalMessages;

            Assert.GreaterOrEqual(originalId, 0, "Message should have a valid ID");

            for (int i = 0; i < 5; ++i)
            {
                DxMessagingStaticState.Reset();

                int idAfterReset = MessageHelperIndexer<MultiResetMessage>.SequentialId;
                int totalAfterReset = MessageHelperIndexer.TotalMessages;

                Assert.AreEqual(
                    originalId,
                    idAfterReset,
                    "Message type ID should be preserved after reset cycle {0}",
                    i + 1
                );
                Assert.AreEqual(
                    originalTotal,
                    totalAfterReset,
                    "TotalMessages should be preserved after reset cycle {0}",
                    i + 1
                );
            }
        }

        [UnityTest]
        public IEnumerator MessageRoutingRemainsCorrectAfterMultipleResets()
        {
            int messageACount = 0;
            int messageBCount = 0;

            MessageRegistrationHandle handleA =
                _token.RegisterUntargeted<MultiResetRoutingMessageA>(
                    (ref MultiResetRoutingMessageA msg) =>
                    {
                        ++messageACount;
                    }
                );
            MessageRegistrationHandle handleB =
                _token.RegisterUntargeted<MultiResetRoutingMessageB>(
                    (ref MultiResetRoutingMessageB msg) =>
                    {
                        ++messageBCount;
                    }
                );

            MultiResetRoutingMessageA messageA = new MultiResetRoutingMessageA();
            MultiResetRoutingMessageB messageB = new MultiResetRoutingMessageB();

            _messageBus.UntargetedBroadcast(ref messageA);
            Assert.AreEqual(1, messageACount, "Message A should be received");
            Assert.AreEqual(0, messageBCount, "Message B should not be received when A is sent");

            _messageBus.UntargetedBroadcast(ref messageB);
            Assert.AreEqual(1, messageACount, "Message A count should not change when B is sent");
            Assert.AreEqual(1, messageBCount, "Message B should be received");

            for (int i = 0; i < 3; ++i)
            {
                DxMessagingStaticState.Reset();

                int expectedACount = 2 + i;
                int expectedBCount = 2 + i;

                _messageBus.UntargetedBroadcast(ref messageA);
                Assert.AreEqual(
                    expectedACount,
                    messageACount,
                    "Message A should route correctly after reset cycle {0}",
                    i + 1
                );
                Assert.AreEqual(
                    expectedBCount - 1,
                    messageBCount,
                    "Message B count should not change when A is sent after reset cycle {0}",
                    i + 1
                );

                _messageBus.UntargetedBroadcast(ref messageB);
                Assert.AreEqual(
                    expectedACount,
                    messageACount,
                    "Message A count should not change when B is sent after reset cycle {0}",
                    i + 1
                );
                Assert.AreEqual(
                    expectedBCount,
                    messageBCount,
                    "Message B should route correctly after reset cycle {0}",
                    i + 1
                );
            }

            yield return null;
            _token.RemoveRegistration(handleA);
            _token.RemoveRegistration(handleB);
        }

        [Test]
        public void SequentialIdAssignmentIsMonotonicallyIncreasing()
        {
            MessageCache<object> cache = new MessageCache<object>();

            cache.GetOrAdd<MonotonicMessageA>();
            int idA = MessageHelperIndexer<MonotonicMessageA>.SequentialId;

            cache.GetOrAdd<MonotonicMessageB>();
            int idB = MessageHelperIndexer<MonotonicMessageB>.SequentialId;

            cache.GetOrAdd<MonotonicMessageC>();
            int idC = MessageHelperIndexer<MonotonicMessageC>.SequentialId;

            Assert.Greater(
                idB,
                idA,
                "Later registered message types should have higher IDs (B > A)"
            );
            Assert.Greater(
                idC,
                idB,
                "Later registered message types should have higher IDs (C > B)"
            );

            DxMessagingStaticState.Reset();

            cache.GetOrAdd<MonotonicMessageD>();
            int idD = MessageHelperIndexer<MonotonicMessageD>.SequentialId;

            Assert.Greater(
                idD,
                idC,
                "New message type registered after reset should have higher ID than pre-reset types"
            );
        }

        private struct RoutingTestMessage : IUntargetedMessage { }

        private struct DistinctMessageA : IUntargetedMessage { }

        private struct DistinctMessageB : IUntargetedMessage { }

        private struct DistinctMessageC : IUntargetedMessage { }

        private struct PreResetMessage : IUntargetedMessage { }

        private struct PostResetMessage : IUntargetedMessage { }

        private struct MultiResetMessage : IUntargetedMessage { }

        private struct MultiResetRoutingMessageA : IUntargetedMessage { }

        private struct MultiResetRoutingMessageB : IUntargetedMessage { }

        private struct MonotonicMessageA : IUntargetedMessage { }

        private struct MonotonicMessageB : IUntargetedMessage { }

        private struct MonotonicMessageC : IUntargetedMessage { }

        private struct MonotonicMessageD : IUntargetedMessage { }

        private struct PostResetRoutingMessage : IUntargetedMessage { }
    }
}
#endif
