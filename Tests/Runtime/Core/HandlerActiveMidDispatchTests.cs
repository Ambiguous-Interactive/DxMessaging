#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Pins the granularity of the live <c>MessageHandler.active</c> check
    /// during dispatch when a handler deactivates ITSELF mid-emission and a
    /// second delegate on the SAME handler at the SAME priority follows it.
    /// </summary>
    /// <remarks>
    /// Untargeted dispatch checks <c>active</c> per DELEGATE (the flattened
    /// snapshot stores one entry per delegate), so the second delegate is
    /// skipped - "deactivation takes effect immediately", per the documented
    /// semantics. Targeted and broadcast dispatch still check <c>active</c>
    /// once per handler bucket entry and then run all of that handler's
    /// delegates, so the second delegate still fires; those kinds will adopt
    /// the per-delegate granularity when their dispatch paths are flattened,
    /// at which point this fixture's expectations unify.
    /// </remarks>
    public sealed class HandlerActiveMidDispatchTests : MessagingTestBase
    {
        [Test]
        public void UntargetedSelfDeactivationSkipsSameHandlerSamePriorityPeer()
        {
            GameObject host = new(
                nameof(UntargetedSelfDeactivationSkipsSameHandlerSamePriorityPeer)
            );
            _spawned.Add(host);
            MessageHandler handler = new(host) { active = true };
            MessageBus bus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();

            int firstCount = 0;
            int secondCount = 0;
            bool deactivateOnInvoke = true;
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (ref SimpleUntargetedMessage _) =>
                {
                    ++firstCount;
                    if (deactivateOnInvoke)
                    {
                        handler.active = false;
                    }
                },
                priority: 0
            );
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (ref SimpleUntargetedMessage _) => ++secondCount,
                priority: 0
            );

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(bus);

            Assert.AreEqual(1, firstCount, "The deactivating delegate must run once.");
            Assert.AreEqual(
                0,
                secondCount,
                "Untargeted dispatch checks active per delegate: a handler that "
                    + "deactivates itself mid-emission must skip its own remaining "
                    + "delegates, even at the same priority."
            );

            deactivateOnInvoke = false;
            handler.active = true;
            message.EmitUntargeted(bus);
            Assert.AreEqual(2, firstCount, "Reactivated handler must dispatch again.");
            Assert.AreEqual(
                1,
                secondCount,
                "After reactivation both delegates must fire (positive control)."
            );

            token.Dispose();
        }

        [Test]
        public void TargetedSelfDeactivationStillRunsSameHandlerSamePriorityPeer()
        {
            GameObject host = new(
                nameof(TargetedSelfDeactivationStillRunsSameHandlerSamePriorityPeer)
            );
            _spawned.Add(host);
            InstanceId hostId = host;
            MessageHandler handler = new(host) { active = true };
            MessageBus bus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();

            int firstCount = 0;
            int secondCount = 0;
            _ = token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                host,
                (ref SimpleTargetedMessage _) =>
                {
                    ++firstCount;
                    handler.active = false;
                },
                priority: 0
            );
            _ = token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                host,
                (ref SimpleTargetedMessage _) => ++secondCount,
                priority: 0
            );

            SimpleTargetedMessage message = new();
            message.EmitGameObjectTargeted(host, bus);

            Assert.AreEqual(1, firstCount, "The deactivating delegate must run once.");
            Assert.AreEqual(
                1,
                secondCount,
                "Targeted dispatch (not yet flattened) checks active once per "
                    + "handler bucket entry, so the same handler's remaining "
                    + "delegates still run this emission. This pin is expected to "
                    + "move to the per-delegate semantics when targeted dispatch "
                    + "is flattened."
            );

            message.EmitGameObjectTargeted(host, bus);
            Assert.AreEqual(
                1,
                firstCount,
                "A deactivated handler must not dispatch on the next emission."
            );

            handler.active = true;
            token.Dispose();
        }
    }
}
#endif
