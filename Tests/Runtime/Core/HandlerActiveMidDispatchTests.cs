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
    /// Every flattened dispatch kind (untargeted, targeted, and broadcast;
    /// handle and post-process) checks <c>active</c> per DELEGATE - the
    /// flattened snapshot stores one entry per delegate - so the second
    /// delegate is skipped: "deactivation takes effect immediately", per the
    /// documented semantics. This is the consciously unified granularity
    /// adopted when targeted/broadcast dispatch was flattened (stage 2);
    /// before that, those kinds checked <c>active</c> once per handler bucket
    /// entry and the same handler's remaining delegates still fired.
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
        public void TargetedSelfDeactivationSkipsSameHandlerSamePriorityPeer()
        {
            GameObject host = new(nameof(TargetedSelfDeactivationSkipsSameHandlerSamePriorityPeer));
            _spawned.Add(host);
            InstanceId hostId = host;
            MessageHandler handler = new(host) { active = true };
            MessageBus bus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();

            int firstCount = 0;
            int secondCount = 0;
            bool deactivateOnInvoke = true;
            _ = token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                host,
                (ref SimpleTargetedMessage _) =>
                {
                    ++firstCount;
                    if (deactivateOnInvoke)
                    {
                        handler.active = false;
                    }
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
                0,
                secondCount,
                "Targeted dispatch (flattened) checks active per delegate: a "
                    + "handler that deactivates itself mid-emission must skip its "
                    + "own remaining delegates, even at the same priority - the "
                    + "same granularity as untargeted dispatch."
            );

            message.EmitGameObjectTargeted(host, bus);
            Assert.AreEqual(
                1,
                firstCount,
                "A deactivated handler must not dispatch on the next emission."
            );

            deactivateOnInvoke = false;
            handler.active = true;
            message.EmitGameObjectTargeted(host, bus);
            Assert.AreEqual(2, firstCount, "Reactivated handler must dispatch again.");
            Assert.AreEqual(
                1,
                secondCount,
                "After reactivation both delegates must fire (positive control)."
            );

            token.Dispose();
        }
    }
}
#endif
