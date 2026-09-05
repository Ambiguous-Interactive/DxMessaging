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
    /// Standalone logs isolate history behavior. The settings integration test restores
    /// its scoped provider override before destroying the temporary settings asset.
    /// </summary>
    [TestFixture]
    public sealed class RegistrationLogTests
    {
        private static readonly InstanceId FirstOwner = new(101);
        private static readonly InstanceId SecondOwner = new(202);
        private static readonly InstanceId UnknownOwner = new(303);

        [Test]
        public void CapturedEmptyViewRemainsLiveThroughWrapResizeAndClear()
        {
            RegistrationLog log = new RegistrationLog(false, 3);
            IReadOnlyList<MessagingRegistration> view = log.Registrations;
            Assert.That(view, Is.Empty, "A newly captured view must start empty.");
            Assert.That(
                log.Registrations,
                Is.SameAs(view),
                "Repeated reads must return the same live view."
            );
            log.Enabled = true;
            for (int index = 0; index < 4; index++)
            {
                log.Log(CreateRegistration(new InstanceId(index), typeof(SimpleUntargetedMessage)));
            }
            CollectionAssert.AreEqual(
                new[] { 1, 2, 3 },
                view.Select(entry => entry.id.Id),
                "A view captured before the first entry must observe the wrapped history."
            );
            Assert.That(
                view[0].id.Id,
                Is.EqualTo(1),
                "Index zero must return the oldest retained entry."
            );
            Assert.That(
                view[2].id.Id,
                Is.EqualTo(3),
                "The final index must return the newest retained entry."
            );
            log.Resize(2);
            CollectionAssert.AreEqual(
                new[] { 2, 3 },
                view.Select(entry => entry.id.Id),
                "The captured view must observe the newest entries after shrinking."
            );
            log.Resize(0);
            Assert.That(view, Is.Empty, "The captured view must observe zero capacity.");
            log.Resize(3);
            log.Log(CreateRegistration(FirstOwner, typeof(SimpleUntargetedMessage)));
            Assert.That(
                view.Single().id,
                Is.EqualTo(FirstOwner),
                "The captured view must observe recording after capacity grows."
            );
            log.Clear();
            Assert.That(view, Is.Empty, "The captured view must observe clearing.");
        }

        [TestCase(0, -1)]
        [TestCase(0, 0)]
        [TestCase(1, -1)]
        [TestCase(1, 1)]
        [TestCase(3, 3)]
        [TestCase(3, int.MaxValue)]
        public void LiveViewRejectsIndicesOutsideCurrentHistory(int count, int index)
        {
            RegistrationLog log = new RegistrationLog(true, 3);
            IReadOnlyList<MessagingRegistration> view = log.Registrations;
            for (int entry = 0; entry < count; entry++)
            {
                log.Log(CreateRegistration(new InstanceId(entry), typeof(SimpleUntargetedMessage)));
            }
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => _ = view[index],
                $"count={count}, index={index}: invalid list indices must preserve the public exception contract."
            );
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        public void LogRetainsOnlyNewestEntriesInChronologicalOrder(int capacity)
        {
            RegistrationLog log = new RegistrationLog(true, capacity);
            for (int i = 0; i < 10; ++i)
            {
                log.Log(CreateRegistration(new InstanceId(i), typeof(SimpleUntargetedMessage)));
            }
            Assert.That(
                log.Registrations.Count,
                Is.EqualTo(capacity),
                $"capacity={capacity}: history must remain bounded."
            );
            CollectionAssert.AreEqual(
                Enumerable.Range(10 - capacity, capacity),
                log.Registrations.Select(entry => entry.id.Id),
                $"capacity={capacity}: wrapping must preserve the newest chronological window."
            );
        }

        [Test]
        public void ResizingAndFilteringWrappedLogPreservesChronologicalHistory()
        {
            RegistrationLog log = new RegistrationLog(true, 3);
            for (int i = 0; i < 6; ++i)
            {
                log.Log(CreateRegistration(new InstanceId(i), typeof(SimpleUntargetedMessage)));
            }
            log.Resize(2);
            Assert.That(
                log.ToString(entry => entry.id.Id.ToString()),
                Is.EqualTo("[4, 5]"),
                "Shrinking must keep the newest entries."
            );
            Assert.That(
                log.Clear(entry => entry.id.Id % 2 == 0),
                Is.EqualTo(1),
                "Filtering must remove only the matching retained entry."
            );
            Assert.That(
                log.Registrations.Single().id.Id,
                Is.EqualTo(5),
                "Filtering a wrapped log must preserve its survivor."
            );
            Assert.That(log.Clear(), Is.EqualTo(1), "Full clear must report the retained count.");
            log.Resize(0);
            log.Log(CreateRegistration(FirstOwner, typeof(SimpleUntargetedMessage)));
            Assert.That(log.Registrations, Is.Empty, "Zero capacity must discard history.");
            log.Resize(2);
            log.Log(CreateRegistration(SecondOwner, typeof(SimpleUntargetedMessage)));
            Assert.That(
                log.Registrations.Single().id,
                Is.EqualTo(SecondOwner),
                "Growing from zero must resume recording without restoring discarded entries."
            );
        }

        [Test]
        public void RegistrationMetadataKeepsIdentityWithoutRetainingUnityObject()
        {
#if UNITY_EDITOR
            UnityEngine.GameObject owner = UnityEditor.EditorUtility.CreateGameObjectWithHideFlags(
                "RegistrationHistoryOwner",
                UnityEngine.HideFlags.HideAndDontSave
            );
#else
            UnityEngine.GameObject owner = new UnityEngine.GameObject("RegistrationHistoryOwner");
#endif
            try
            {
                InstanceId id = owner;
                MessagingRegistration entry = CreateRegistration(
                    id,
                    typeof(SimpleUntargetedMessage)
                );
                Assert.That(
                    entry.id,
                    Is.EqualTo(id),
                    "Removing the object reference must preserve numeric identity."
                );
                Assert.That(
                    ReferenceEquals(entry.id.Object, null),
                    Is.True,
                    "Diagnostic history must not root a Unity object wrapper."
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void RuntimeSettingsResizeExistingAndNewRegistrationLogs()
        {
            DxMessaging.Core.Configuration.DxMessagingRuntimeSettings settings =
                UnityEngine.ScriptableObject.CreateInstance<DxMessaging.Core.Configuration.DxMessagingRuntimeSettings>();
            System.IDisposable settingsOverride = null;
            try
            {
                MessageBus existing = new MessageBus();
                existing.Log.Enabled = true;
                settings._registrationLogCapacity = 2;
                settingsOverride =
                    DxMessaging.Core.Configuration.DxMessagingRuntimeSettingsProvider.Override(
                        settings
                    );
                MessageBus later = new MessageBus();
                later.Log.Enabled = true;
                foreach (MessageBus bus in new[] { existing, later })
                {
                    for (int i = 0; i < 4; ++i)
                    {
                        bus.Log.Log(
                            CreateRegistration(new InstanceId(i), typeof(SimpleUntargetedMessage))
                        );
                    }
                    Assert.That(
                        bus.Log.Registrations.Count,
                        Is.EqualTo(2),
                        "Both existing and newly created buses must use the configured bound."
                    );
                }
                settings._registrationLogCapacity = 1;
                DxMessaging.Core.Configuration.DxMessagingRuntimeSettings.RaiseSettingsChanged(
                    settings
                );
                foreach (MessageBus bus in new[] { existing, later })
                {
                    Assert.That(
                        bus.Log.Registrations.Single().id.Id,
                        Is.EqualTo(3),
                        "A live capacity reduction must preserve only the newest entry."
                    );
                }
            }
            finally
            {
                settingsOverride?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

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
