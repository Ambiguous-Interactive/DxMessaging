namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Regression coverage for the v4 O(1) bus deregister fast-path
    /// (<c>MessageBus.DeregisterScalarHandler</c> / <c>DeregisterContextHandler</c>) and the cold
    /// fallback's "do not touch the live bucket" contract.
    ///
    /// <para>
    /// The fast-path operates on the leaf <c>HandlerCache</c> captured on the registration handle
    /// (<see cref="MessageBusRegistration"/>) directly, deferring sink re-resolution to a cold
    /// fallback hit only when the handler is NOT found in the captured bucket (a post-sweep silent
    /// no-op or a genuine over-deregistration). Cursor Bugbot flagged a Medium-severity bug in an
    /// earlier revision: the fallback bumped <c>version</c> on whichever leaf cache currently
    /// occupies the priority, so after a full deregister followed by a NEW registration at the same
    /// (type, priority) -- which builds a fresh bucket -- a stale over-deregister with the OLD
    /// handle would increment the NEW live bucket's version (a spurious dispatch-snapshot rebuild,
    /// and a stale handle reaching into a slot it has no business touching). The fix removed the
    /// fallback version bump entirely (the fallback is a pure no-op classification; nothing is
    /// removed, so nothing needs invalidating).
    /// </para>
    /// <para>
    /// These tests reproduce that exact scenario at the bus API level (unreachable through the
    /// public <see cref="MessageRegistrationToken"/>, which guards double-deregistration, and
    /// through the <c>MessageHandler</c> facade, which guards it again before the bus is reached).
    /// <see cref="StaleOverDeregisterLeavesReusedSlotHandlerFunctional"/> pins the behavioural
    /// invariant (the live handler at the reused slot keeps dispatching); the reflection-based
    /// <see cref="StaleOverDeregisterDoesNotBumpReusedBucketVersion"/> pins the specific fix (the
    /// live bucket's version is untouched), in the same intentional-private-name style as
    /// <c>CounterBasedTouchTests</c>.
    /// </para>
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class StaleOverDeregistrationTests
    {
        private static readonly InstanceId OwnerOne = new InstanceId(0x57A1_0001);
        private static readonly InstanceId OwnerTwo = new InstanceId(0x57A1_0002);

        private readonly struct ProbeMessage : IUntargetedMessage<ProbeMessage> { }

        private bool _savedMessagingDebug;

        [SetUp]
        public void DisableMessagingDebug()
        {
            // The stale over-deregistration below is a genuine over-deregistration, which logs an
            // error when diagnostics are on. Keep diagnostics off so the intentional over-dereg is
            // the silent no-op production path and does not trip the test runner's LogError gate.
            _savedMessagingDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
        }

        [TearDown]
        public void RestoreMessagingDebug()
        {
            MessagingDebug.enabled = _savedMessagingDebug;
        }

        [Test]
        public void StaleOverDeregisterLeavesReusedSlotHandlerFunctional()
        {
            MessageBus bus = new MessageBus();
            MessageHandler ownerOne = new MessageHandler(OwnerOne, bus) { active = true };
            MessageHandler ownerTwo = new MessageHandler(OwnerTwo, bus) { active = true };

            // Owner one occupies (ProbeMessage, priority 0), then fully deregisters so the priority
            // bucket is removed.
            MessageBusRegistration ownerOneRegistration = bus.RegisterUntargeted<ProbeMessage>(
                ownerOne,
                priority: 0
            );
            bus.Deregister<ProbeMessage>(in ownerOneRegistration);

            // Owner two registers a live handler at the SAME (type, priority) -- a brand-new bucket.
            int ownerTwoInvocations = 0;
            Action<ProbeMessage> ownerTwoHandler = _ => ownerTwoInvocations++;
            _ = ownerTwo.RegisterUntargetedMessageHandler<ProbeMessage>(
                ownerTwoHandler,
                ownerTwoHandler,
                priority: 0,
                messageBus: bus
            );

            // Stale over-deregistration with owner one's old handle: the handler is not in the new
            // bucket, so this hits the cold fallback. It must NOT disturb owner two's live handler.
            bus.Deregister<ProbeMessage>(in ownerOneRegistration);

            ProbeMessage message = default;
            bus.UntargetedBroadcast(ref message);

            Assert.AreEqual(
                1,
                ownerTwoInvocations,
                "A stale over-deregistration of a handle whose (type, priority) slot was reused by a "
                    + "different handler must leave the live handler fully functional."
            );

            ownerOne.active = false;
            ownerTwo.active = false;
        }

        [Test]
        public void StaleOverDeregisterDoesNotBumpReusedBucketVersion()
        {
            MessageBus bus = new MessageBus();
            MessageHandler ownerOne = new MessageHandler(OwnerOne, bus) { active = true };
            MessageHandler ownerTwo = new MessageHandler(OwnerTwo, bus) { active = true };

            MessageBusRegistration ownerOneRegistration = bus.RegisterUntargeted<ProbeMessage>(
                ownerOne,
                priority: 0
            );
            bus.Deregister<ProbeMessage>(in ownerOneRegistration);

            // A second bus-level registration so we hold the NEW bucket's handle for reflection.
            MessageBusRegistration ownerTwoRegistration = bus.RegisterUntargeted<ProbeMessage>(
                ownerTwo,
                priority: 0
            );

            long versionBefore = ReadCapturedBucketVersion(ownerTwoRegistration, priority: 0);
            Assert.That(
                versionBefore,
                Is.GreaterThanOrEqualTo(0),
                "Could not read the live bucket version; the internal layout changed -- update this "
                    + "guard (see CounterBasedTouchTests for the intentional-private-name policy)."
            );

            // Stale over-deregistration: hits the cold fallback (owner one is not in owner two's
            // bucket). It must NOT bump the live bucket's version (no membership change occurred).
            bus.Deregister<ProbeMessage>(in ownerOneRegistration);

            long versionAfter = ReadCapturedBucketVersion(ownerTwoRegistration, priority: 0);

            Assert.AreEqual(
                versionBefore,
                versionAfter,
                "The cold deregister fallback (handler not found) is a no-op classification path and "
                    + "must not bump the version of the live bucket now occupying that priority."
            );

            ownerOne.active = false;
            ownerTwo.active = false;
        }

        /// <summary>
        /// Reads the per-priority leaf bucket's <c>version</c> via the per-type
        /// <c>HandlerCache&lt;int, HandlerCache&gt;</c> captured on the registration handle
        /// (<see cref="MessageBusRegistration"/>'s internal <c>capturedPrimary</c> field). Returns
        /// -1 if any step of the (intentionally private) layout could not be navigated.
        /// </summary>
        private static long ReadCapturedBucketVersion(
            MessageBusRegistration registration,
            int priority
        )
        {
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            FieldInfo capturedPrimaryField = typeof(MessageBusRegistration).GetField(
                "capturedPrimary",
                flags
            );
            if (capturedPrimaryField == null)
            {
                return -1;
            }

            object perTypeCache = capturedPrimaryField.GetValue(registration);
            if (perTypeCache == null)
            {
                return -1;
            }

            FieldInfo handlersField = perTypeCache.GetType().GetField("handlers", flags);
            if (
                handlersField == null
                || !(handlersField.GetValue(perTypeCache) is System.Collections.IDictionary buckets)
            )
            {
                return -1;
            }

            if (!buckets.Contains(priority))
            {
                return -1;
            }

            object bucket = buckets[priority];
            if (bucket == null)
            {
                return -1;
            }

            FieldInfo versionField = bucket.GetType().GetField("version", flags);
            if (versionField == null)
            {
                return -1;
            }

            return (long)versionField.GetValue(bucket);
        }
    }
}
