#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Pins the lifecycle state machine of <see cref="MessageRegistrationLease"/>:
    /// idempotent Activate/Deactivate/Dispose, ObjectDisposedException on Activate
    /// after Dispose, and the consistent, recoverable state left behind when an
    /// OnActivate lifecycle callback throws. Every test uses an isolated
    /// <see cref="MessageBus"/> so no state leaks into the global bus.
    /// </summary>
    [TestFixture]
    public sealed class MessageRegistrationLeaseTests
    {
        private MessageBus _bus;
        private MessageRegistrationBuilder _builder;

        [SetUp]
        public void SetUp()
        {
            _bus = new MessageBus();
            _builder = new MessageRegistrationBuilder(new FixedMessageBusProvider(_bus));
        }

        [TearDown]
        public void TearDown()
        {
            _builder = null;
            _bus = null;
        }

        [Test]
        public void ActivateAfterDisposeThrowsObjectDisposedException()
        {
            MessageRegistrationLease lease = _builder.Build(new MessageRegistrationBuildOptions());
            lease.Dispose();

            Assert.Throws<ObjectDisposedException>(
                lease.Activate,
                "Activate on a disposed lease must throw ObjectDisposedException."
            );
        }

        [Test]
        public void DoubleActivateIsIdempotent()
        {
            int activations = 0;
            int handled = 0;
            MessageRegistrationBuildOptions options = new()
            {
                Configure = token =>
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(null, _ => ++activations, null, null),
            };

            using MessageRegistrationLease lease = _builder.Build(options);
            lease.Activate();
            lease.Activate();

            Assert.AreEqual(
                1,
                activations,
                "OnActivate must fire exactly once across repeated Activate calls."
            );
            Assert.IsTrue(lease.IsActive, "Lease must report active after Activate.");
            Assert.IsTrue(lease.Token.Enabled, "Token must be enabled after Activate.");

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(_bus);
            Assert.AreEqual(
                1,
                handled,
                "Double Activate must not double-register the staged handler."
            );

            lease.Deactivate();
        }

        [Test]
        public void DoubleDeactivateIsIdempotent()
        {
            int deactivations = 0;
            int handled = 0;
            MessageRegistrationBuildOptions options = new()
            {
                ActivateOnBuild = true,
                Configure = token =>
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    null,
                    _ => ++deactivations,
                    null
                ),
            };

            using MessageRegistrationLease lease = _builder.Build(options);
            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(_bus);
            Assert.AreEqual(1, handled, "Control failed: active lease must dispatch.");

            lease.Deactivate();
            lease.Deactivate();

            Assert.AreEqual(
                1,
                deactivations,
                "OnDeactivate must fire exactly once across repeated Deactivate calls."
            );
            Assert.IsFalse(lease.IsActive, "Lease must report inactive after Deactivate.");

            message.EmitUntargeted(_bus);
            Assert.AreEqual(1, handled, "Deactivated lease must not dispatch.");
        }

        [Test]
        public void DeactivateBeforeActivateIsNoOp()
        {
            int deactivations = 0;
            MessageRegistrationBuildOptions options = new()
            {
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    null,
                    _ => ++deactivations,
                    null
                ),
            };

            using MessageRegistrationLease lease = _builder.Build(options);
            Assert.DoesNotThrow(
                lease.Deactivate,
                "Deactivate on a never-activated lease must be a no-op."
            );
            Assert.AreEqual(
                0,
                deactivations,
                "OnDeactivate must not fire when the lease was never active."
            );
            Assert.IsFalse(lease.IsActive, "Lease must stay inactive.");
        }

        [Test]
        public void DoubleDisposeInvokesOnDisposeOnce()
        {
            int disposals = 0;
            MessageRegistrationBuildOptions options = new()
            {
                Lifecycle = new MessageRegistrationLifecycle(null, null, null, _ => ++disposals),
            };

            MessageRegistrationLease lease = _builder.Build(options);
            lease.Dispose();
            Assert.DoesNotThrow(lease.Dispose, "Second Dispose must be a harmless no-op.");
            Assert.AreEqual(
                1,
                disposals,
                "OnDispose must fire exactly once across repeated Dispose calls."
            );
        }

        [Test]
        public void OnActivateThrowLeavesLeaseActiveAndRecoverable()
        {
            // Activate() marks the lease active BEFORE invoking OnActivate, so a
            // throwing callback propagates to the caller but leaves the lease's
            // state consistent with the registrations: IsActive is true, the
            // registrations are live, and Deactivate()/Dispose() fully release
            // them. (A previous implementation set _isActive only after the
            // callback, which wedged live registrations behind an inactive lease.)
            int handled = 0;
            MessageRegistrationBuildOptions options = new()
            {
                Configure = token =>
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    _ => throw new InvalidOperationException("OnActivate failure"),
                    null,
                    null
                ),
            };

            MessageRegistrationLease lease = _builder.Build(options);
            Assert.Throws<InvalidOperationException>(
                lease.Activate,
                "The OnActivate exception must propagate to the Activate caller."
            );

            Assert.IsTrue(
                lease.IsActive,
                "A throwing OnActivate must leave the lease ACTIVE: the activation "
                    + "state must match the live registrations so teardown works."
            );
            Assert.IsTrue(
                lease.Token.Enabled,
                "The token was enabled before OnActivate threw and stays enabled."
            );
            Assert.IsTrue(
                lease.Handler.active,
                "The handler was activated before OnActivate threw and stays active."
            );

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(_bus);
            Assert.AreEqual(
                1,
                handled,
                "Registrations are live, consistent with the lease reporting active."
            );

            lease.Deactivate();
            Assert.IsFalse(
                lease.IsActive,
                "Deactivate after a throwing OnActivate must release the lease."
            );
            message.EmitUntargeted(_bus);
            Assert.AreEqual(
                1,
                handled,
                "Deactivate must release the live registrations even though "
                    + "OnActivate threw - no wedged handler leak."
            );

            lease.Dispose();
            message.EmitUntargeted(_bus);
            Assert.AreEqual(
                1,
                handled,
                "Dispose after recovery must keep the registrations released."
            );
        }

        [Test]
        public void ReactivateAfterOnActivateThrowRecoveryCompletes()
        {
            bool throwOnActivate = true;
            int activations = 0;
            int handled = 0;
            MessageRegistrationBuildOptions options = new()
            {
                Configure = token =>
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    _ =>
                    {
                        ++activations;
                        if (throwOnActivate)
                        {
                            throw new InvalidOperationException("OnActivate failure");
                        }
                    },
                    null,
                    null
                ),
            };

            using MessageRegistrationLease lease = _builder.Build(options);
            Assert.Throws<InvalidOperationException>(lease.Activate);
            Assert.IsTrue(
                lease.IsActive,
                "A throwing OnActivate leaves the lease active (state matches the "
                    + "live registrations)."
            );

            // Recovery cycle: release the half-activated lease, then activate
            // cleanly. The second Activate must re-run OnActivate because the
            // lease passed through Deactivate first.
            lease.Deactivate();
            Assert.IsFalse(lease.IsActive, "Deactivate must release the thrown-into lease.");

            throwOnActivate = false;
            Assert.DoesNotThrow(
                lease.Activate,
                "Reactivation after a Deactivate recovery must succeed."
            );
            Assert.IsTrue(lease.IsActive, "The reactivation must complete activation.");
            Assert.AreEqual(2, activations, "OnActivate must have run once per attempt.");

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(_bus);
            Assert.AreEqual(
                1,
                handled,
                "The reactivation must not double-register the staged handler."
            );

            lease.Deactivate();
            Assert.IsFalse(lease.IsActive, "Deactivate must work after a successful retry.");
            message.EmitUntargeted(_bus);
            Assert.AreEqual(1, handled, "Deactivated lease must not dispatch after retry.");
        }
    }
}
#endif
