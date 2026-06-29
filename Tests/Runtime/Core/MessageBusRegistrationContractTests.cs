#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Pins the v4 opaque-handle contract for <see cref="MessageBusRegistration"/>: the
    /// <see cref="MessageBusRegistration.None"/> sentinel, the external/DIY constructor
    /// round-trip, value equality, and the "foreign / empty handle deregistration is a silent
    /// no-op" guarantee on the built-in <see cref="MessageBus"/>.
    /// </summary>
    public sealed class MessageBusRegistrationContractTests
    {
        [Test]
        public void NoneHandleIsInvalidAndEqualsDefault()
        {
            MessageBusRegistration none = MessageBusRegistration.None;
            Assert.IsFalse(none.IsValid, "None must be invalid.");
            Assert.AreEqual(default(MessageBusRegistration), none, "None must equal default.");
            Assert.IsTrue(none == default, "None == default must hold.");
            Assert.IsFalse(none != default, "None != default must be false.");
            Assert.AreEqual(none.GetHashCode(), default(MessageBusRegistration).GetHashCode());
        }

        [Test]
        public void ExternalConstructorRoundTripsIdAndState()
        {
            object state = new();
            MessageBusRegistration external = new(42L, state);
            Assert.IsTrue(external.IsValid, "An external handle is a live (valid) handle.");
            Assert.AreEqual(42L, external.ExternalId);
            Assert.AreSame(state, external.ExternalState);
        }

        [Test]
        public void NonExternalHandleExposesNoExternalPayload()
        {
            MessageBusRegistration none = MessageBusRegistration.None;
            Assert.AreEqual(0L, none.ExternalId);
            Assert.IsNull(none.ExternalState);
        }

        [Test]
        public void EqualityIsValueBased()
        {
            object state = new();
            MessageBusRegistration a = new(7L, state);
            MessageBusRegistration b = new(7L, state);
            MessageBusRegistration c = new(8L, state);
            Assert.AreEqual(a, b, "Same id + same state ref must be equal.");
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.AreNotEqual(a, c, "Different id must be unequal.");
            Assert.IsTrue(a.Equals((object)b));
            Assert.IsFalse(a.Equals("not a handle"));
        }

        [Test]
        public void DeregisterNoneOnMessageBusIsSilentNoOp()
        {
            IMessageBus bus = MessageHandler.MessageBus;
            MessageBusRegistration none = MessageBusRegistration.None;
            Assert.DoesNotThrow(() => bus.Deregister<SimpleUntargetedMessage>(in none));
        }

        [Test]
        public void DeregisterExternalHandleOnMessageBusIsSilentNoOp()
        {
            IMessageBus bus = MessageHandler.MessageBus;
            MessageBusRegistration external = new(123L, "owned-by-a-foreign-bus");
            // A handle minted by a custom IMessageBus (kind == External) owns no store on the
            // built-in MessageBus, so deregistering it here must be a no-op (no throw).
            Assert.DoesNotThrow(() => bus.Deregister<SimpleUntargetedMessage>(in external));
        }

        [Test]
        public void RefcountRegistrationsMintDistinctHandles()
        {
            GameObject go = new(nameof(RefcountRegistrationsMintDistinctHandles));
            try
            {
                MessageHandler handler = new(go) { active = true };
                IMessageBus bus = MessageHandler.MessageBus;

                // Two distinct Register* calls for the SAME (handler, type, priority) bump the
                // handler's refcount. Pre-v4 each returned a distinct deregistration delegate;
                // the v4 handles must likewise be UNEQUAL so a consumer that stores handles in a
                // set/map and deregisters per-unique-handle does not under-deregister (leak).
                MessageBusRegistration first = bus.RegisterUntargeted<SimpleUntargetedMessage>(
                    handler
                );
                MessageBusRegistration second = bus.RegisterUntargeted<SimpleUntargetedMessage>(
                    handler
                );
                try
                {
                    Assert.AreNotEqual(
                        first,
                        second,
                        "Two refcount registrations must mint unequal handles."
                    );
                    Assert.IsTrue(first != second);
                }
                finally
                {
                    bus.Deregister<SimpleUntargetedMessage>(in first);
                    bus.Deregister<SimpleUntargetedMessage>(in second);
                    handler.active = false;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
#endif
