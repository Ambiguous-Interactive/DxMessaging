#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Proves the load-bearing safety invariant of the post-processor closure collapse:
    /// for each collapsed <see cref="System.Action{T}"/> post-processor family the token
    /// folds diagnostics into a single by-ref flat invoker and stores the RAW user
    /// handler as the dedup/identity key (<c>entry.handler</c>), so dispatch must go
    /// through the diagnostics-carrying flat invoker, never the raw handler.
    /// <para>
    /// Each test enables token diagnostics, registers an <c>Action</c> post-processor,
    /// emits, and asserts the token's per-registration call count recorded the
    /// invocation. The raw user handler does NOT touch <c>_callCounts</c>; only the
    /// diagnostics-augmented flat invoker does. So a non-zero recorded count is direct
    /// proof that the augmented invoker -- not the raw handler -- is the live dispatch
    /// target. If a future change routed the default post-process slot through
    /// <c>entry.handler</c> (as the global-accept-all slot does), these counts would stay
    /// zero and the tests fail. This closes the diagnostics+post-processor coverage gap
    /// the collapse work surfaced.
    /// </para>
    /// </summary>
    public sealed class PostProcessorDiagnosticsTests
    {
        private const int OwnerInstanceId = 41;
        private const int ContextInstanceId = 43;
        private const int Emissions = 3;

        private static MessageRegistrationToken NewDiagnosticToken(
            out MessageBus bus,
            out MessageHandler handler
        )
        {
            bus = new MessageBus();
            handler = new MessageHandler(new InstanceId(OwnerInstanceId), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();
            token.DiagnosticMode = true;
            return token;
        }

        // The diagnostics-augmented flat invoker records into token._callCounts; a
        // registration whose closure never ran has no entry (count 0). TryGetValue
        // keeps this dependency-free (no CollectionExtensions.GetValueOrDefault).
        private static int CallCount(
            MessageRegistrationToken token,
            MessageRegistrationHandle handle
        )
        {
            token._callCounts.TryGetValue(handle, out int count);
            return count;
        }

        [Test]
        public void PostProcessorForTargetDispatchesDiagnosticsAugmentedClosure()
        {
            MessageRegistrationToken token = NewDiagnosticToken(out MessageBus bus, out _);
            InstanceId target = new(ContextInstanceId);

            int ran = 0;
            MessageRegistrationHandle handle =
                token.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                    target,
                    (SimpleTargetedMessage _) => ++ran
                );

            for (int i = 0; i < Emissions; ++i)
            {
                SimpleTargetedMessage message = new();
                message.EmitTargeted(target, bus);
            }

            Assert.AreEqual(Emissions, ran, "Control: the targeted post-processor must have run.");
            Assert.AreEqual(
                Emissions,
                CallCount(token, handle),
                "The diagnostics-augmented flat invoker (not the raw Action handler) must be "
                    + "the live dispatch target for the collapsed targeted post-processor; a zero "
                    + "count means the raw handler was dispatched instead and diagnostics were lost."
            );
        }

        [Test]
        public void PostProcessorWithoutTargetingDispatchesDiagnosticsAugmentedClosure()
        {
            MessageRegistrationToken token = NewDiagnosticToken(out MessageBus bus, out _);
            InstanceId target = new(ContextInstanceId);

            int ran = 0;
            MessageRegistrationHandle handle =
                token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                    (InstanceId _, SimpleTargetedMessage _) => ++ran
                );

            for (int i = 0; i < Emissions; ++i)
            {
                SimpleTargetedMessage message = new();
                message.EmitTargeted(target, bus);
            }

            Assert.AreEqual(
                Emissions,
                ran,
                "Control: the targeted-without-targeting post-processor must have run."
            );
            Assert.AreEqual(
                Emissions,
                CallCount(token, handle),
                "The diagnostics-augmented context flat invoker (not the raw Action handler) must "
                    + "be the live dispatch target for the collapsed targeted-without-targeting "
                    + "post-processor."
            );
        }

        [Test]
        public void PostProcessorForSourceDispatchesDiagnosticsAugmentedClosure()
        {
            MessageRegistrationToken token = NewDiagnosticToken(out MessageBus bus, out _);
            InstanceId source = new(ContextInstanceId);

            int ran = 0;
            MessageRegistrationHandle handle =
                token.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                    source,
                    (SimpleBroadcastMessage _) => ++ran
                );

            for (int i = 0; i < Emissions; ++i)
            {
                SimpleBroadcastMessage message = new();
                message.EmitBroadcast(source, bus);
            }

            Assert.AreEqual(Emissions, ran, "Control: the broadcast post-processor must have run.");
            Assert.AreEqual(
                Emissions,
                CallCount(token, handle),
                "The diagnostics-augmented flat invoker (not the raw Action handler) must be the "
                    + "live dispatch target for the collapsed sourced-broadcast post-processor."
            );
        }

        [Test]
        public void PostProcessorWithoutSourceDispatchesDiagnosticsAugmentedClosure()
        {
            MessageRegistrationToken token = NewDiagnosticToken(out MessageBus bus, out _);
            InstanceId source = new(ContextInstanceId);

            int ran = 0;
            MessageRegistrationHandle handle =
                token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                    (InstanceId _, SimpleBroadcastMessage _) => ++ran
                );

            for (int i = 0; i < Emissions; ++i)
            {
                SimpleBroadcastMessage message = new();
                message.EmitBroadcast(source, bus);
            }

            Assert.AreEqual(
                Emissions,
                ran,
                "Control: the broadcast-without-source post-processor must have run."
            );
            Assert.AreEqual(
                Emissions,
                CallCount(token, handle),
                "The diagnostics-augmented context flat invoker (not the raw Action handler) must "
                    + "be the live dispatch target for the collapsed broadcast-without-source "
                    + "post-processor."
            );
        }
    }
}
#endif
