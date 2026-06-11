#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using NUnit.Framework;

    /// <summary>
    /// Pins the public comparability surface of <see cref="MessageHandler"/>:
    /// equality is defined by owner <see cref="InstanceId"/> (not by instance
    /// identity), GetHashCode derives from the owner and is stable, and CompareTo
    /// orders handlers by owner id. CompareTo against null or a non-handler object
    /// returns -1 as documented.
    /// </summary>
    [TestFixture]
    public sealed class MessageHandlerComparabilityTests
    {
        [Test]
        public void EqualsIsReflexiveSymmetricAndDefinedByOwnerId()
        {
            MessageHandler first = new(new InstanceId(7));
            MessageHandler sameOwner = new(new InstanceId(7));
            MessageHandler differentOwner = new(new InstanceId(8));

            Assert.IsTrue(first.Equals(first), "Equality must be reflexive.");
            Assert.IsTrue(
                first.Equals(sameOwner),
                "Two distinct handler instances with the same owner id must be equal."
            );
            Assert.IsTrue(sameOwner.Equals(first), "Owner-id equality must be symmetric.");
            Assert.IsFalse(
                first.Equals(differentOwner),
                "Handlers with different owner ids must not be equal."
            );
            Assert.IsFalse(differentOwner.Equals(first), "Owner-id inequality must be symmetric.");
            Assert.IsFalse(first.Equals((MessageHandler)null), "Equals(null) must be false.");
        }

        [Test]
        public void ObjectEqualsOverloadMatchesTypedEquals()
        {
            MessageHandler first = new(new InstanceId(7));
            MessageHandler sameOwner = new(new InstanceId(7));

            Assert.IsTrue(
                first.Equals((object)sameOwner),
                "Equals(object) must agree with the typed overload for an equal handler."
            );
            Assert.IsFalse(first.Equals((object)null), "Equals(object) must be false for null.");
            Assert.IsFalse(
                first.Equals("not a handler"),
                "Equals(object) must be false for a non-handler argument."
            );
        }

        [Test]
        public void GetHashCodeDerivesFromOwnerAndIsStable()
        {
            InstanceId owner = new(42);
            MessageHandler handler = new(owner);
            MessageHandler sameOwner = new(owner);

            Assert.AreEqual(
                owner.GetHashCode(),
                handler.GetHashCode(),
                "The handler hash must equal the owner id hash."
            );
            Assert.AreEqual(
                handler.GetHashCode(),
                handler.GetHashCode(),
                "The hash must be stable across repeated calls."
            );
            Assert.AreEqual(
                handler.GetHashCode(),
                sameOwner.GetHashCode(),
                "Equal handlers (same owner id) must produce equal hashes."
            );
        }

        [Test]
        public void CompareToOrdersByOwnerIdAndIsConsistentWithInstanceId()
        {
            InstanceId lowerId = new(1);
            InstanceId higherId = new(2);
            MessageHandler lower = new(lowerId);
            MessageHandler higher = new(higherId);
            MessageHandler lowerTwin = new(lowerId);

            Assert.AreEqual(
                Math.Sign(lowerId.CompareTo(higherId)),
                Math.Sign(lower.CompareTo(higher)),
                "Handler CompareTo must agree with the owner InstanceId ordering."
            );
            Assert.Less(
                lower.CompareTo(higher),
                0,
                "A handler with a lower owner id must order before a higher one."
            );
            Assert.Greater(
                higher.CompareTo(lower),
                0,
                "A handler with a higher owner id must order after a lower one."
            );
            Assert.AreEqual(
                -Math.Sign(higher.CompareTo(lower)),
                Math.Sign(lower.CompareTo(higher)),
                "CompareTo must be antisymmetric."
            );
            Assert.AreEqual(0, lower.CompareTo(lower), "CompareTo(self) must be zero.");
            Assert.AreEqual(
                0,
                lower.CompareTo(lowerTwin),
                "CompareTo between equal-owner handlers must be zero."
            );
        }

        [Test]
        public void CompareToNullOrNonHandlerReturnsMinusOne()
        {
            // Pinned, documented behavior: CompareTo returns -1 for null and for
            // non-handler objects. Note this inverts the usual IComparable
            // convention (where any instance compares GREATER than null); the XML
            // docs on MessageHandler.CompareTo state -1 explicitly, so the
            // implementation matches its documentation.
            MessageHandler handler = new(new InstanceId(7));

            Assert.AreEqual(
                -1,
                handler.CompareTo((MessageHandler)null),
                "CompareTo(null handler) must return -1 as documented."
            );
            Assert.AreEqual(
                -1,
                handler.CompareTo((object)null),
                "CompareTo(null object) must return -1 as documented."
            );
            Assert.AreEqual(
                -1,
                handler.CompareTo("not a handler"),
                "CompareTo(non-handler object) must return -1 as documented."
            );
        }

        [Test]
        public void SortingHandlersOrdersThemByOwnerId()
        {
            MessageHandler third = new(new InstanceId(3));
            MessageHandler first = new(new InstanceId(1));
            MessageHandler second = new(new InstanceId(2));
            List<MessageHandler> handlers = new() { third, first, second };

            handlers.Sort();

            Assert.IsTrue(
                handlers.Select(handler => handler.owner.Id).SequenceEqual(new[] { 1, 2, 3 }),
                "Sorting handlers must order them by ascending owner id; got [{0}].",
                string.Join(", ", handlers.Select(handler => handler.owner.Id))
            );
        }
    }
}
#endif
