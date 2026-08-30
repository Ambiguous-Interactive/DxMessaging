#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Threading;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>
    /// Pins the documented threading contract: bus operations are not guaranteed
    /// thread-safe. These tests do not assert correctness under concurrency; they
    /// only assert that current behavior does not change silently. If a future
    /// change introduces real thread-safety enforcement (e.g. throwing on misuse),
    /// the sentinel will fail and force a deliberate review of the contract.
    /// </summary>
    [TestFixture]
    public sealed class SingleThreadContractTests
    {
        private const int OwnerInstanceId = 1;
        private const int SerialOwnerInstanceId = 2;
        private const int BackgroundJoinTimeoutMilliseconds = 2000;

        [Test]
        public void BusOperationFromNonMainThreadDoesNotCrash()
        {
            BusType bus = new BusType();
            MessageHandler handler = new MessageHandler(new InstanceId(OwnerInstanceId), bus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int invocationCount = 0;
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (in SimpleUntargetedMessage _) => Interlocked.Increment(ref invocationCount)
            );
            token.Enable();

            Exception captured = null;
            Thread worker = new Thread(() =>
            {
                try
                {
                    SimpleUntargetedMessage message = new();
                    bus.UntargetedBroadcast(ref message);
                }
                catch (Exception e)
                {
                    captured = e;
                }
            })
            {
                IsBackground = true,
                Name = "DxMessagingNonMainThreadSentinel",
            };

            worker.Start();
            bool joined = worker.Join(BackgroundJoinTimeoutMilliseconds);

            Assert.IsTrue(
                joined,
                "Background bus operation must terminate within the join timeout."
            );

            // Pinning current behavior: the bus does not enforce a threading contract.
            // The dispatch path has no thread checks, so the handler is expected to
            // run on the worker thread without any framework-level exception. We
            // require strictly that no exception escapes - if one does, the test
            // fails with full diagnostics so the contract change is reviewed.
            if (captured != null)
            {
                Assert.Fail(
                    $"Background thread emission produced unexpected exception: {captured}"
                );
            }

            // Contract pins that no exception escapes; the handler runs on the
            // worker thread under cross-thread misuse so the counter should advance
            // at least once. Tearing reads are possible in theory but the lone
            // worker scenario is not concurrent enough to exhibit them.
            Assert.GreaterOrEqual(
                invocationCount,
                1,
                "Handler should have been invoked at least once before any potential failure."
            );

            token.Dispose();
        }

        /// <summary>
        /// Determinism smoke check (not a concurrency test): a long sequence of
        /// serial emissions on the main thread must produce exactly one handler
        /// invocation per emission with no drift, drop, or double-fire.
        /// </summary>
        [Test]
        public void RepeatedSerialEmitProducesDeterministicCounts()
        {
            BusType bus = new BusType();
            MessageHandler handler = new MessageHandler(new InstanceId(SerialOwnerInstanceId), bus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            int invocationCount = 0;
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (in SimpleUntargetedMessage _) => ++invocationCount
            );
            token.Enable();

            const int Iterations = 50;
            for (int i = 0; i < Iterations; ++i)
            {
                SimpleUntargetedMessage message = new();
                bus.UntargetedBroadcast(ref message);
            }

            Assert.AreEqual(
                Iterations,
                invocationCount,
                "Repeated single-thread emissions must dispatch deterministically."
            );

            token.Dispose();
        }
    }
}
#endif
