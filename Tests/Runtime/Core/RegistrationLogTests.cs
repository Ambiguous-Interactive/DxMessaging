#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Behavioral pins for <see cref="RegistrationLog"/>: the Enabled toggle gates
    /// recording, <see cref="RegistrationLog.GetRegistrations"/> filters by
    /// <see cref="InstanceId"/> preserving insertion order, and
    /// <see cref="RegistrationLog.ToString(System.Func{MessagingRegistration, string})"/>
    /// applies the supplied formatter (falling back to the default when null).
    /// Every test uses a standalone log instance, so global bus state is untouched.
    /// </summary>
    [TestFixture]
    public sealed class RegistrationLogTests
    {
        private static readonly InstanceId FirstOwner = new(101);
        private static readonly InstanceId SecondOwner = new(202);
        private static readonly InstanceId UnknownOwner = new(303);

        [Test]
        public void LogIsDisabledByDefaultAndIgnoresEntries()
        {
            RegistrationLog log = new();
            Assert.IsFalse(log.Enabled, "A default-constructed log must start disabled.");

            log.Log(CreateRegistration(FirstOwner, typeof(SimpleUntargetedMessage)));

            Assert.AreEqual(0, log.Registrations.Count, "A disabled log must ignore Log calls.");
            Assert.AreEqual("[]", log.ToString(), "An empty log must serialize as [].");
        }

        [Test]
        public void EnabledConstructorArgumentStartsRecordingImmediately()
        {
            RegistrationLog log = new(enabled: true);
            Assert.IsTrue(log.Enabled, "The enabled constructor argument must apply.");

            log.Log(CreateRegistration(FirstOwner, typeof(SimpleUntargetedMessage)));

            Assert.AreEqual(1, log.Registrations.Count, "An enabled log must record entries.");
            Assert.AreEqual(
                FirstOwner,
                log.Registrations[0].id,
                "The recorded entry must carry the logged owner id."
            );
        }

        [Test]
        public void EnabledToggleGatesRecordingMidStream()
        {
            RegistrationLog log = new(enabled: true);
            log.Log(CreateRegistration(FirstOwner, typeof(SimpleUntargetedMessage)));

            log.Enabled = false;
            log.Log(CreateRegistration(SecondOwner, typeof(SimpleTargetedMessage)));
            Assert.AreEqual(
                1,
                log.Registrations.Count,
                "Entries logged while disabled must be dropped."
            );

            log.Enabled = true;
            log.Log(CreateRegistration(FirstOwner, typeof(SimpleBroadcastMessage)));

            Assert.AreEqual(
                2,
                log.Registrations.Count,
                "Re-enabling must resume recording without resurrecting dropped entries."
            );
            Assert.AreEqual(
                typeof(SimpleUntargetedMessage),
                log.Registrations[0].type,
                "The first recorded entry must be the pre-disable one."
            );
            Assert.AreEqual(
                typeof(SimpleBroadcastMessage),
                log.Registrations[1].type,
                "The second recorded entry must be the post-re-enable one."
            );
        }

        [Test]
        public void GetRegistrationsFiltersByInstanceIdPreservingOrder()
        {
            RegistrationLog log = new(enabled: true);
            log.Log(CreateRegistration(FirstOwner, typeof(SimpleUntargetedMessage)));
            log.Log(CreateRegistration(SecondOwner, typeof(SimpleTargetedMessage)));
            log.Log(
                CreateRegistration(
                    FirstOwner,
                    typeof(SimpleBroadcastMessage),
                    RegistrationType.Deregister
                )
            );

            List<MessagingRegistration> firstOwnerEntries = log.GetRegistrations(FirstOwner)
                .ToList();

            Assert.AreEqual(
                2,
                firstOwnerEntries.Count,
                "GetRegistrations must return only the entries for the requested id."
            );
            Assert.AreEqual(
                typeof(SimpleUntargetedMessage),
                firstOwnerEntries[0].type,
                "Filtered entries must preserve insertion order (first entry)."
            );
            Assert.AreEqual(
                typeof(SimpleBroadcastMessage),
                firstOwnerEntries[1].type,
                "Filtered entries must preserve insertion order (second entry)."
            );
            Assert.AreEqual(
                RegistrationType.Deregister,
                firstOwnerEntries[1].registrationType,
                "Filtered entries must carry their original registration type."
            );
            Assert.IsTrue(
                firstOwnerEntries.All(entry => entry.id == FirstOwner),
                "Every filtered entry must match the requested id."
            );

            Assert.IsFalse(
                log.GetRegistrations(UnknownOwner).Any(),
                "GetRegistrations for an id that never registered must be empty."
            );
        }

        [Test]
        public void ToStringAppliesCustomSerializerToEveryEntry()
        {
            RegistrationLog log = new(enabled: true);
            log.Log(CreateRegistration(FirstOwner, typeof(SimpleUntargetedMessage)));
            log.Log(CreateRegistration(SecondOwner, typeof(SimpleTargetedMessage)));

            string formatted = log.ToString(registration => registration.type.Name);

            Assert.AreEqual(
                "[SimpleUntargetedMessage, SimpleTargetedMessage]",
                formatted,
                "The custom serializer must be applied to every entry, joined with "
                    + "comma-space inside brackets."
            );
        }

        [Test]
        public void ToStringWithNullSerializerMatchesDefaultToString()
        {
            RegistrationLog log = new(enabled: true);
            log.Log(CreateRegistration(FirstOwner, typeof(SimpleUntargetedMessage)));

            Assert.AreEqual(
                log.ToString(),
                log.ToString(null),
                "A null serializer must fall back to the default MessagingRegistration "
                    + "formatting."
            );
            StringAssert.Contains(
                typeof(SimpleUntargetedMessage).FullName,
                log.ToString(),
                "Default formatting must include the registered message type."
            );
        }

        [Test]
        public void EmptyLogSerializesAsBracketsForBothToStringOverloads()
        {
            RegistrationLog log = new(enabled: true);
            Assert.AreEqual("[]", log.ToString(), "Default ToString of an empty log must be [].");
            Assert.AreEqual(
                "[]",
                log.ToString(registration => "never-invoked"),
                "Custom-serializer ToString of an empty log must be [] without invoking "
                    + "the serializer."
            );
        }

        [Test]
        public void ClearRemovesMatchingEntriesAndReportsCount()
        {
            RegistrationLog log = new(enabled: true);
            log.Log(CreateRegistration(FirstOwner, typeof(SimpleUntargetedMessage)));
            log.Log(CreateRegistration(SecondOwner, typeof(SimpleTargetedMessage)));
            log.Log(CreateRegistration(FirstOwner, typeof(SimpleBroadcastMessage)));

            int removed = log.Clear(registration => registration.id == FirstOwner);

            Assert.AreEqual(2, removed, "Predicate Clear must report the removed count.");
            Assert.AreEqual(
                1,
                log.Registrations.Count,
                "Predicate Clear must keep non-matching entries."
            );
            Assert.AreEqual(
                SecondOwner,
                log.Registrations[0].id,
                "The surviving entry must be the non-matching one."
            );

            int removedAll = log.Clear();
            Assert.AreEqual(1, removedAll, "Parameterless Clear must report the removed count.");
            Assert.AreEqual(0, log.Registrations.Count, "Parameterless Clear must empty the log.");
        }

        private static MessagingRegistration CreateRegistration(
            InstanceId id,
            System.Type messageType,
            RegistrationType registrationType = RegistrationType.Register
        )
        {
            return new MessagingRegistration(
                id,
                messageType,
                registrationType,
                RegistrationMethod.Untargeted
            );
        }
    }
}
#endif
