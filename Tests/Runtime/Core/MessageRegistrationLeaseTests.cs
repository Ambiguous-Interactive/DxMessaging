#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
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
        public void DisposeClearsOwnedTokenDiagnosticsAndStagedRegistrations()
        {
            using DiagnosticsScope diagnosticsScope = new(
                diagnosticsTargets: DiagnosticsTarget.Off,
                messageBufferSize: 4
            );
            int handled = 0;
            MessageRegistrationHandle handle = default;
            MessageRegistrationBuildOptions options = new()
            {
                ActivateOnBuild = true,
                EnableDiagnostics = true,
                Configure = token =>
                {
                    handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                },
            };

            using (
                LeakWatcher watcher = new(
                    bus: _bus,
                    throwOnLeak: true,
                    label: nameof(DisposeClearsOwnedTokenDiagnosticsAndStagedRegistrations)
                )
            )
            {
                MessageRegistrationLease lease = _builder.Build(options);
                SimpleUntargetedMessage message = new();
                message.EmitUntargeted(_bus);
                Assert.AreEqual(1, handled, "Control failed: active lease must dispatch.");
                AssertTokenDiagnosticsPopulated(lease.Token, handle);

                lease.Dispose();
                Assert.IsFalse(lease.IsActive, "Disposed lease must report inactive.");
                Assert.IsFalse(lease.Token.Enabled, "Disposing the lease must disable its token.");
                Assert.AreEqual(0, _bus.RegisteredUntargeted, "Dispose must deregister handlers.");
                AssertTokenDiagnosticsEmpty(lease.Token);

                lease.Token.Enable();
                message.EmitUntargeted(_bus);
                Assert.AreEqual(
                    1,
                    handled,
                    "Directly enabling the token after lease disposal must not resurrect handlers."
                );
            }
        }

        [Test]
        public void ThrowingOnDisposeStillClearsOwnedToken()
        {
            using DiagnosticsScope diagnosticsScope = new(
                diagnosticsTargets: DiagnosticsTarget.Off,
                messageBufferSize: 4
            );
            int handled = 0;
            MessageRegistrationHandle handle = default;
            MessageRegistrationBuildOptions options = new()
            {
                ActivateOnBuild = true,
                EnableDiagnostics = true,
                Configure = token =>
                {
                    handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    null,
                    null,
                    _ => throw new InvalidOperationException("OnDispose failure")
                ),
            };

            using (
                LeakWatcher watcher = new(
                    bus: _bus,
                    throwOnLeak: true,
                    label: nameof(ThrowingOnDisposeStillClearsOwnedToken)
                )
            )
            {
                MessageRegistrationLease lease = _builder.Build(options);
                SimpleUntargetedMessage message = new();
                message.EmitUntargeted(_bus);
                Assert.AreEqual(1, handled, "Control failed: active lease must dispatch.");
                AssertTokenDiagnosticsPopulated(lease.Token, handle);

                Assert.Throws<InvalidOperationException>(
                    lease.Dispose,
                    "The OnDispose exception must propagate to the Dispose caller."
                );
                Assert.IsFalse(lease.IsActive, "Dispose cleanup must mark the lease inactive.");
                Assert.IsFalse(lease.Token.Enabled, "Dispose cleanup must disable the token.");
                Assert.AreEqual(0, _bus.RegisteredUntargeted, "Dispose must deregister handlers.");
                AssertTokenDiagnosticsEmpty(lease.Token);

                lease.Token.Enable();
                message.EmitUntargeted(_bus);
                Assert.AreEqual(1, handled, "Token cleanup must run even when OnDispose throws.");
                Assert.DoesNotThrow(
                    lease.Dispose,
                    "A second Dispose after a thrown OnDispose is a no-op."
                );
            }
        }

        [Test]
        public void ThrowingOnDeactivateStillInvokesOnDisposeAndClearsOwnedToken()
        {
            using DiagnosticsScope diagnosticsScope = new(
                diagnosticsTargets: DiagnosticsTarget.Off,
                messageBufferSize: 4
            );
            int handled = 0;
            int disposals = 0;
            MessageRegistrationHandle handle = default;
            MessageRegistrationBuildOptions options = new()
            {
                ActivateOnBuild = true,
                EnableDiagnostics = true,
                Configure = token =>
                {
                    handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    null,
                    _ => throw new InvalidOperationException("OnDeactivate failure"),
                    _ => ++disposals
                ),
            };

            using (
                LeakWatcher watcher = new(
                    bus: _bus,
                    throwOnLeak: true,
                    label: nameof(ThrowingOnDeactivateStillInvokesOnDisposeAndClearsOwnedToken)
                )
            )
            {
                MessageRegistrationLease lease = _builder.Build(options);
                SimpleUntargetedMessage message = new();
                message.EmitUntargeted(_bus);
                Assert.AreEqual(1, handled, "Control failed: active lease must dispatch.");
                AssertTokenDiagnosticsPopulated(lease.Token, handle);

                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                    lease.Dispose,
                    "The OnDeactivate exception must propagate to the Dispose caller."
                );
                Assert.AreEqual("OnDeactivate failure", exception.Message);
                Assert.AreEqual(
                    1,
                    disposals,
                    "OnDispose must still run when OnDeactivate fails during Dispose."
                );
                Assert.IsFalse(
                    lease.IsActive,
                    "Successful token cleanup must mark the lease inactive."
                );
                Assert.IsFalse(lease.Token.Enabled, "Dispose cleanup must disable the token.");
                Assert.AreEqual(0, _bus.RegisteredUntargeted, "Dispose must deregister handlers.");
                AssertTokenDiagnosticsEmpty(lease.Token);
            }
        }

        [Test]
        public void ThrowingOnDeactivateAndDeregistrationRethrowsOnDeactivate()
        {
            using DiagnosticsScope diagnosticsScope = new(
                diagnosticsTargets: DiagnosticsTarget.Off,
                messageBufferSize: 4
            );
            MessageBus innerBus = new();
            FailingDeregistrationBus throwingBus = new(innerBus);
            MessageRegistrationBuilder builder = new(new FixedMessageBusProvider(throwingBus));
            int handled = 0;
            int disposals = 0;
            MessageRegistrationHandle handle = default;
            MessageRegistrationBuildOptions options = new()
            {
                ActivateOnBuild = true,
                EnableDiagnostics = true,
                Configure = token =>
                {
                    handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    null,
                    _ => throw new InvalidOperationException("OnDeactivate failure"),
                    _ => ++disposals
                ),
            };

            using (
                LeakWatcher watcher = new(
                    bus: throwingBus,
                    throwOnLeak: true,
                    label: nameof(ThrowingOnDeactivateAndDeregistrationRethrowsOnDeactivate)
                )
            )
            {
                MessageRegistrationLease lease = builder.Build(options);
                SimpleUntargetedMessage message = new();
                throwingBus.UntargetedBroadcast(ref message);
                Assert.AreEqual(1, handled, "Control failed: active lease must dispatch.");
                AssertTokenDiagnosticsPopulated(lease.Token, handle);

                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                    lease.Dispose,
                    "The OnDeactivate exception must win over token deregistration failure."
                );
                Assert.AreEqual("OnDeactivate failure", exception.Message);
                Assert.AreEqual(1, disposals, "OnDispose must still run.");
                Assert.IsTrue(
                    lease.IsActive,
                    "Failed token cleanup must keep the lease active for retry."
                );
                Assert.IsTrue(
                    lease.Token.Enabled,
                    "A failed token deregistration must keep cleanup retryable."
                );
                Assert.AreEqual(
                    1,
                    throwingBus.RegisteredUntargeted,
                    "The failing deregistration must leave the handler live until retry."
                );
                Assert.IsTrue(
                    lease.Token._metadata.ContainsKey(handle),
                    "The failed cleanup must keep token metadata retryable."
                );

                throwingBus.AllowDeregistrations();
                Assert.DoesNotThrow(
                    lease.Dispose,
                    "Lease Dispose must retry failed token cleanup."
                );
                Assert.IsFalse(
                    lease.Token.Enabled,
                    "Retrying token cleanup must disable the token."
                );
                Assert.IsFalse(lease.IsActive, "Successful retry must mark the lease inactive.");
                Assert.AreEqual(0, throwingBus.RegisteredUntargeted, "Retry must deregister.");
                AssertTokenDiagnosticsEmpty(lease.Token);
            }
        }

        [Test]
        public void ThrowingOnDisposeAndDeregistrationRethrowsOnDispose()
        {
            using DiagnosticsScope diagnosticsScope = new(
                diagnosticsTargets: DiagnosticsTarget.Off,
                messageBufferSize: 4
            );
            MessageBus innerBus = new();
            FailingDeregistrationBus throwingBus = new(innerBus);
            MessageRegistrationBuilder builder = new(new FixedMessageBusProvider(throwingBus));
            int handled = 0;
            MessageRegistrationHandle handle = default;
            MessageRegistrationBuildOptions options = new()
            {
                EnableDiagnostics = true,
                Configure = token =>
                {
                    handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    null,
                    null,
                    _ => throw new InvalidOperationException("OnDispose failure")
                ),
            };

            using (
                LeakWatcher watcher = new(
                    bus: throwingBus,
                    throwOnLeak: true,
                    label: nameof(ThrowingOnDisposeAndDeregistrationRethrowsOnDispose)
                )
            )
            {
                MessageRegistrationLease lease = builder.Build(options);
                lease.Token.Enable();
                SimpleUntargetedMessage message = new();
                throwingBus.UntargetedBroadcast(ref message);
                Assert.AreEqual(1, handled, "Control failed: active token must dispatch.");
                AssertTokenDiagnosticsPopulated(lease.Token, handle);

                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                    lease.Dispose,
                    "The OnDispose exception must win over token cleanup failure."
                );
                Assert.AreEqual("OnDispose failure", exception.Message);
                Assert.IsTrue(
                    lease.IsActive,
                    "Failed token cleanup must keep the lease active for retry."
                );
                Assert.IsTrue(
                    lease.Token.Enabled,
                    "A failed token deregistration must keep cleanup retryable."
                );
                Assert.AreEqual(
                    1,
                    throwingBus.RegisteredUntargeted,
                    "The failing deregistration must leave the handler live until retry."
                );
                Assert.IsTrue(
                    lease.Token._metadata.ContainsKey(handle),
                    "The failed cleanup must keep token metadata retryable."
                );

                throwingBus.AllowDeregistrations();
                Assert.DoesNotThrow(
                    lease.Dispose,
                    "Lease Dispose must retry failed token cleanup."
                );
                Assert.IsFalse(
                    lease.Token.Enabled,
                    "Retrying token cleanup must disable the token."
                );
                Assert.IsFalse(lease.IsActive, "Successful retry must mark the lease inactive.");
                Assert.AreEqual(0, throwingBus.RegisteredUntargeted, "Retry must deregister.");
                AssertTokenDiagnosticsEmpty(lease.Token);
            }
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

        [Test]
        public void ActivateReplayFailureWithRollbackFailureCanDeactivateThroughLease()
        {
            MessageBus innerBus = new();
            ThrowingRegistrationWithFailingRollbackBus throwingBus = new(
                innerBus,
                successfulRegistrationsBeforeThrow: 1
            );
            MessageRegistrationBuilder builder = new(new FixedMessageBusProvider(throwingBus));
            int handled = 0;
            int deactivations = 0;
            MessageRegistrationBuildOptions options = new()
            {
                Configure = token =>
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 0
                    );
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 1
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    null,
                    _ => ++deactivations,
                    null
                ),
            };

            using (
                LeakWatcher watcher = new(
                    bus: throwingBus,
                    throwOnLeak: true,
                    label: nameof(ActivateReplayFailureWithRollbackFailureCanDeactivateThroughLease)
                )
            )
            {
                using MessageRegistrationLease lease = builder.Build(options);
                try
                {
                    Assert.Throws<InvalidOperationException>(
                        lease.Activate,
                        "The throwing bus must fail during activation replay."
                    );
                    Assert.IsTrue(
                        lease.IsActive,
                        "A failed activation that leaves live token registrations must mark the lease active."
                    );
                    Assert.IsTrue(
                        lease.Token.Enabled,
                        "A rollback cleanup failure must leave token cleanup retryable."
                    );
                    Assert.AreEqual(
                        1,
                        throwingBus.RegisteredUntargeted,
                        "The failed rollback registration must remain live until lease cleanup retry."
                    );

                    SimpleUntargetedMessage message = new();
                    throwingBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        1,
                        handled,
                        "The live partial registration must dispatch before cleanup retry."
                    );

                    throwingBus.AllowDeregistrations();
                    lease.Deactivate();
                    Assert.IsFalse(lease.IsActive, "Lease Deactivate must retry cleanup.");
                    Assert.IsFalse(
                        lease.Token.Enabled,
                        "Lease cleanup retry must disable the token."
                    );
                    Assert.AreEqual(1, deactivations, "OnDeactivate must run for the live lease.");
                    Assert.AreEqual(
                        0,
                        throwingBus.RegisteredUntargeted,
                        "Lease cleanup retry must deregister the live partial registration."
                    );
                }
                finally
                {
                    throwingBus.AllowDeregistrations();
                }
            }
        }

        [Test]
        public void ActivateOnBuildOnActivateThrowCleansOwnedLeaseBeforeThrowingAgain()
        {
            int handled = 0;
            int deactivations = 0;
            int disposals = 0;
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
                    _ => throw new InvalidOperationException("OnActivate failure"),
                    _ => ++deactivations,
                    _ => ++disposals
                ),
            };

            using (
                LeakWatcher watcher = new(
                    bus: _bus,
                    throwOnLeak: true,
                    label: nameof(ActivateOnBuildOnActivateThrowCleansOwnedLeaseBeforeThrowingAgain)
                )
            )
            {
                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                    () => _builder.Build(options),
                    "Build must throw the original OnActivate failure when cleanup succeeds."
                );
                Assert.AreEqual("OnActivate failure", exception.Message);
                Assert.AreEqual(
                    1,
                    deactivations,
                    "Build must deactivate the owned lease before throwing again."
                );
                Assert.AreEqual(
                    1,
                    disposals,
                    "Build must dispose the owned lease before throwing again."
                );
                Assert.AreEqual(
                    0,
                    _bus.RegisteredUntargeted,
                    "Build failure cleanup must deregister."
                );

                SimpleUntargetedMessage message = new();
                message.EmitUntargeted(_bus);
                Assert.AreEqual(0, handled, "Cleaned build failure must not leave live handlers.");
            }
        }

        [Test]
        public void ActivateOnBuildReplayFailureWithRollbackFailureTransfersLeaseForCleanupRetry()
        {
            MessageBus innerBus = new();
            ThrowingRegistrationWithFailingRollbackBus throwingBus = new(
                innerBus,
                successfulRegistrationsBeforeThrow: 1
            );
            MessageRegistrationBuilder builder = new(new FixedMessageBusProvider(throwingBus));
            int handled = 0;
            int deactivations = 0;
            int disposals = 0;
            MessageRegistrationBuildOptions options = new()
            {
                ActivateOnBuild = true,
                Configure = token =>
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 0
                    );
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++handled,
                        priority: 1
                    );
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    null,
                    null,
                    _ => ++deactivations,
                    _ => ++disposals
                ),
            };

            using (
                LeakWatcher watcher = new(
                    bus: throwingBus,
                    throwOnLeak: true,
                    label: nameof(
                        ActivateOnBuildReplayFailureWithRollbackFailureTransfersLeaseForCleanupRetry
                    )
                )
            )
            {
                MessageRegistrationLease lease = null;
                try
                {
                    MessageRegistrationBuildException exception =
                        Assert.Throws<MessageRegistrationBuildException>(
                            () => builder.Build(options),
                            "Build must transfer the owned lease when automatic cleanup cannot finish."
                        );
                    Assert.IsInstanceOf<InvalidOperationException>(
                        exception.ActivationException,
                        "The exception must preserve the original activation failure."
                    );
                    Assert.AreSame(
                        exception.ActivationException,
                        exception.InnerException,
                        "The exception InnerException must be the original activation failure."
                    );
                    Assert.IsInstanceOf<InvalidOperationException>(
                        exception.CleanupException,
                        "The exception must preserve the cleanup failure that kept the lease live."
                    );
                    lease = exception.Lease;
                    Assert.IsNotNull(lease, "The build exception must expose the retryable lease.");
                    Assert.IsTrue(
                        lease.IsActive,
                        "The transferred lease must report active while cleanup is retryable."
                    );
                    Assert.IsTrue(
                        lease.Token.Enabled,
                        "The transferred token must remain enabled while cleanup is retryable."
                    );
                    Assert.AreEqual(
                        1,
                        throwingBus.RegisteredUntargeted,
                        "The failed rollback registration must remain live until the lease is retried."
                    );
                    Assert.AreEqual(
                        1,
                        deactivations,
                        "Automatic cleanup must have invoked OnDeactivate once."
                    );
                    Assert.AreEqual(
                        1,
                        disposals,
                        "Automatic cleanup must have invoked OnDispose once."
                    );

                    SimpleUntargetedMessage message = new();
                    throwingBus.UntargetedBroadcast(ref message);
                    Assert.AreEqual(
                        1,
                        handled,
                        "The transferred lease must own the live partial registration."
                    );

                    throwingBus.AllowDeregistrations();
                    lease.Dispose();
                    Assert.IsFalse(lease.IsActive, "Retrying Dispose must release the lease.");
                    Assert.IsFalse(lease.Token.Enabled, "Retrying Dispose must disable the token.");
                    Assert.AreEqual(
                        0,
                        throwingBus.RegisteredUntargeted,
                        "Retrying Dispose must deregister the live partial registration."
                    );
                }
                finally
                {
                    throwingBus.AllowDeregistrations();
                    lease?.Dispose();
                }
            }
        }

        private static void AssertTokenDiagnosticsPopulated(
            MessageRegistrationToken token,
            MessageRegistrationHandle handle
        )
        {
            Assert.AreEqual(1, token._metadata.Count, "Expected one staged registration.");
            Assert.IsTrue(token._metadata.ContainsKey(handle), "Metadata must include the handle.");
            Assert.IsTrue(
                token._callCounts.TryGetValue(handle, out int callCount),
                "Call counts must include the handle."
            );
            Assert.AreEqual(1, callCount, "Call count must reflect the control emission.");
            Assert.AreEqual(1, token._emissionBuffer.Count, "Emission history must be populated.");
        }

        private static void AssertTokenDiagnosticsEmpty(MessageRegistrationToken token)
        {
            Assert.AreEqual(0, token._metadata.Count, "Token metadata must be empty.");
            Assert.AreEqual(0, token._callCounts.Count, "Token call counts must be empty.");
            Assert.AreEqual(
                0,
                token._emissionBuffer.Count,
                "Token emission history must be empty."
            );
        }

        private sealed class FailingDeregistrationBus : DelegatingMessageBus
        {
            private bool _throwOnDeregistration = true;

            internal FailingDeregistrationBus(IMessageBus inner)
                : base(inner) { }

            internal void AllowDeregistrations()
            {
                _throwOnDeregistration = false;
            }

            public override Action RegisterUntargeted<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
            {
                Action innerDeregister = base.RegisterUntargeted<T>(messageHandler, priority);
                return () =>
                {
                    if (_throwOnDeregistration && typeof(T) == typeof(SimpleUntargetedMessage))
                    {
                        throw new InvalidOperationException("Deregistration failure.");
                    }

                    innerDeregister();
                };
            }
        }

        private sealed class ThrowingRegistrationWithFailingRollbackBus : DelegatingMessageBus
        {
            private readonly int _successfulRegistrationsBeforeThrow;
            private int _registrationAttempts;
            private bool _throwOnRegistration = true;
            private bool _throwOnDeregistration = true;

            internal ThrowingRegistrationWithFailingRollbackBus(
                IMessageBus inner,
                int successfulRegistrationsBeforeThrow
            )
                : base(inner)
            {
                _successfulRegistrationsBeforeThrow = successfulRegistrationsBeforeThrow;
            }

            internal void AllowDeregistrations()
            {
                _throwOnDeregistration = false;
            }

            public override Action RegisterUntargeted<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
            {
                if (_throwOnRegistration && typeof(T) == typeof(SimpleUntargetedMessage))
                {
                    if (_registrationAttempts == _successfulRegistrationsBeforeThrow)
                    {
                        throw new InvalidOperationException("Registration replay failure.");
                    }

                    ++_registrationAttempts;
                }

                Action innerDeregister = base.RegisterUntargeted<T>(messageHandler, priority);
                return () =>
                {
                    if (_throwOnDeregistration && typeof(T) == typeof(SimpleUntargetedMessage))
                    {
                        throw new InvalidOperationException("Rollback deregistration failure.");
                    }

                    innerDeregister();
                };
            }
        }
    }
}
#endif
