#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading;
    using DxMessaging.Core.Configuration;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>Validation normalizes local values and delivers side effects on a later Editor update.</summary>
    /// <remarks>
    /// 2026-10-19: Synchronous validation inspected assets and notified live buses inside Unity's
    /// loading callback. Keep worker validation free of those calls and retain deferred delivery.
    /// </remarks>
    [Category("Contract")]
    public sealed class RuntimeSettingsValidationTests
    {
        private static readonly Type SettingsType = typeof(DxMessagingRuntimeSettings);
        private static readonly MethodInfo ValidateMethod = SettingsType.GetMethod(
            "OnValidate",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        private static readonly MethodInfo ProcessMethod = SettingsType.GetMethod(
            "ProcessPendingValidations",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        private readonly List<DxMessagingRuntimeSettings> _owned = new();
        private readonly List<DxMessagingRuntimeSettings> _savedPending = new();
        private ConcurrentQueue<DxMessagingRuntimeSettings> _pending;
        private EditorStaticFieldScope _events;

        [SetUp]
        public void PreserveValidationState()
        {
            Assert.That(
                EditorApplication.isCompiling,
                Is.False,
                "Compilation must settle before validation tests."
            );
            Assert.That(
                EditorApplication.isUpdating,
                Is.False,
                "Imports must settle before validation tests."
            );
            Assert.That(
                ProcessMethod,
                Is.Not.Null,
                "Production must own its deferred validation callback."
            );
            FieldInfo pendingField = SettingsType.GetField(
                "PendingValidations",
                BindingFlags.Static | BindingFlags.NonPublic
            );
            Assert.That(
                pendingField,
                Is.Not.Null,
                "Production must retain pending validations until an idle update."
            );
            _pending = (ConcurrentQueue<DxMessagingRuntimeSettings>)pendingField.GetValue(null);
            while (_pending.TryDequeue(out DxMessagingRuntimeSettings settings))
            {
                _savedPending.Add(settings);
            }
            _events = new EditorStaticFieldScope(SettingsType, "SettingsChanged");
            _events.Set("SettingsChanged", null);
        }

        [TearDown]
        public void RestoreValidationState()
        {
            try
            {
                foreach (DxMessagingRuntimeSettings settings in _owned)
                {
                    if (settings != null)
                    {
                        Object.DestroyImmediate(settings);
                    }
                }
                if (_pending != null)
                {
                    List<DxMessagingRuntimeSettings> otherPending = new();
                    while (_pending.TryDequeue(out DxMessagingRuntimeSettings settings))
                    {
                        if (!_owned.Exists(owned => ReferenceEquals(owned, settings)))
                        {
                            otherPending.Add(settings);
                        }
                    }
                    foreach (DxMessagingRuntimeSettings settings in _savedPending)
                    {
                        _pending.Enqueue(settings);
                    }
                    foreach (DxMessagingRuntimeSettings settings in otherPending)
                    {
                        _pending.Enqueue(settings);
                    }
                }
            }
            finally
            {
                _events?.Dispose();
                _events = null;
                _pending = null;
                _owned.Clear();
                _savedPending.Clear();
            }
        }

        [TestCase(1)]
        [TestCase(8)]
        [TestCase(32)]
        public void RepeatedValidationDeliversLatestValuesOnce(int validationCount)
        {
            DxMessagingRuntimeSettings settings = CreateSettings();
            int calls = 0;
            int lastValue = -1;
            DxMessagingRuntimeSettings.SettingsChanged += current =>
            {
                calls++;
                lastValue = current.BufferMaxDistinctEntries;
            };
            for (int i = 0; i < validationCount; ++i)
            {
                settings._bufferMaxDistinctEntries = i;
                Validate(settings);
            }
            Assert.That(
                calls,
                Is.Zero,
                $"{validationCount} validations must defer subscriber side effects."
            );
            Process();
            Assert.That(calls, Is.EqualTo(1), "One asset must produce one pending notification.");
            Assert.That(
                lastValue,
                Is.EqualTo(validationCount - 1),
                "Delivery must observe the final validated values."
            );
            Process();
            Assert.That(
                calls,
                Is.EqualTo(1),
                "An empty update must not repeat a delivered notification."
            );
        }

        [TestCase(1)]
        [TestCase(8)]
        public void WorkerValidationNotifiesOnlyOnEditorThread(int workerCount)
        {
            DxMessagingRuntimeSettings settings = CreateSettings();
            int mainThread = Environment.CurrentManagedThreadId;
            int deliveryThread = -1;
            int calls = 0;
            DxMessagingRuntimeSettings.SettingsChanged += _ =>
            {
                calls++;
                deliveryThread = Environment.CurrentManagedThreadId;
            };
            Exception[] failures = new Exception[workerCount];
            Thread[] workers = new Thread[workerCount];
            for (int i = 0; i < workerCount; ++i)
            {
                int index = i;
                workers[i] = new Thread(() =>
                {
                    try
                    {
                        Validate(settings);
                    }
                    catch (Exception exception)
                    {
                        failures[index] = exception;
                    }
                });
                workers[i].Start();
            }
            foreach (Thread worker in workers)
            {
                worker.Join();
            }
            Assert.That(
                failures,
                Is.All.Null,
                "Worker validation must not call thread-bound Unity APIs."
            );
            Assert.That(calls, Is.Zero, "Worker validation must not run subscribers.");
            Process();
            Assert.That(calls, Is.EqualTo(1), "Concurrent requests for one asset must coalesce.");
            Assert.That(
                deliveryThread,
                Is.EqualTo(mainThread),
                "The Editor callback must own subscriber delivery."
            );
        }

        [Test]
        public void DifferentAssetsRetainFirstQueuedOrder()
        {
            DxMessagingRuntimeSettings first = CreateSettings();
            DxMessagingRuntimeSettings second = CreateSettings();
            List<DxMessagingRuntimeSettings> seen = new();
            DxMessagingRuntimeSettings.SettingsChanged += seen.Add;
            Validate(first);
            Validate(second);
            Validate(first);
            Assert.That(seen, Is.Empty, "Validation must not notify synchronously.");
            Process();
            CollectionAssert.AreEqual(
                new[] { first, second },
                seen,
                "Distinct assets must retain their first queued order."
            );
        }

        [Test]
        public void ReentrantValidationWaitsForAnotherUpdate()
        {
            DxMessagingRuntimeSettings settings = CreateSettings();
            int calls = 0;
            DxMessagingRuntimeSettings.SettingsChanged += current =>
            {
                calls++;
                if (calls == 1)
                {
                    Validate(current);
                }
            };
            Validate(settings);
            Assert.That(calls, Is.Zero, "The initial validation must defer delivery.");
            Process();
            Assert.That(
                calls,
                Is.EqualTo(1),
                "Reentrant validation must not drain inside the same update."
            );
            Process();
            Assert.That(calls, Is.EqualTo(2), "The next update must retain the reentrant request.");
        }

        [Test]
        public void DestroyedQueuedAssetDoesNotNotify()
        {
            DxMessagingRuntimeSettings settings = CreateSettings();
            int calls = 0;
            DxMessagingRuntimeSettings.SettingsChanged += _ => calls++;
            Validate(settings);
            Object.DestroyImmediate(settings);
            Assert.That(
                calls,
                Is.Zero,
                "Delivery must remain deferred until destruction completes."
            );
            Assert.DoesNotThrow(
                Process,
                "A queued destroyed Unity object must be discarded safely."
            );
            Assert.That(calls, Is.Zero, "Destroyed assets must not notify subscribers.");
        }

        [Test]
        public void FallbackCreationDoesNotBroadcastDefaultsLater()
        {
            DxMessagingRuntimeSettings settings = CreateSettings();
            int calls = 0;
            DxMessagingRuntimeSettings.SettingsChanged += _ => calls++;
            Validate(settings);
            settings.MarkAsFallbackInstance();
            Assert.That(calls, Is.Zero, "Fallback creation must not invoke subscribers.");
            Process();
            Assert.That(
                calls,
                Is.Zero,
                "A queued fallback must not overwrite live-bus settings later."
            );
            DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
            Assert.That(
                calls,
                Is.EqualTo(1),
                "Explicit internal notifications must retain synchronous delivery."
            );
        }

        [Test]
        public void SubscriberFailureLeavesOtherAssetsPending()
        {
            DxMessagingRuntimeSettings first = CreateSettings();
            DxMessagingRuntimeSettings second = CreateSettings();
            int calls = 0;
            DxMessagingRuntimeSettings.SettingsChanged += settings =>
            {
                calls++;
                if (ReferenceEquals(settings, first))
                {
                    throw new InvalidOperationException("Expected validation subscriber failure.");
                }
            };
            Validate(first);
            Validate(second);
            Assert.That(calls, Is.Zero, "Subscriber failures must occur outside validation.");
            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(
                Process,
                "The Editor callback must preserve subscriber failures."
            );
            Assert.That(
                failure.InnerException,
                Is.TypeOf<InvalidOperationException>(),
                "The original subscriber exception must remain visible."
            );
            Assert.That(calls, Is.EqualTo(1), "The failing notification must stop this callback.");
            Process();
            Assert.That(
                calls,
                Is.EqualTo(2),
                "Unprocessed assets must survive a subscriber failure."
            );
        }

        [Test]
        public void NegativeValuesClampBeforeDeferredNotification()
        {
            DxMessagingRuntimeSettings settings = CreateSettings();
            settings._idleEvictionSeconds = -1f;
            settings._evictionTickIntervalSeconds = -1f;
            settings._bufferMaxDistinctEntries = -1;
            settings._messageBufferSize = -1;
            settings._registrationLogCapacity = -1;
            int calls = 0;
            DxMessagingRuntimeSettings.SettingsChanged += _ => calls++;
            Validate(settings);
            Assert.That(
                settings.IdleEvictionSeconds,
                Is.Zero,
                "Validation must clamp idle age locally."
            );
            Assert.That(
                settings.EvictionTickIntervalSeconds,
                Is.Zero,
                "Validation must clamp cadence locally."
            );
            Assert.That(
                settings.BufferMaxDistinctEntries,
                Is.Zero,
                "Validation must clamp pool capacity locally."
            );
            Assert.That(
                settings.MessageBufferSize,
                Is.Zero,
                "Validation must clamp message capacity locally."
            );
            Assert.That(
                settings.RegistrationLogCapacity,
                Is.Zero,
                "Validation must clamp log capacity locally."
            );
            Assert.That(calls, Is.Zero, "Normalization must not trigger subscriber side effects.");
        }

        private DxMessagingRuntimeSettings CreateSettings()
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            settings.hideFlags = HideFlags.HideAndDontSave;
            _owned.Add(settings);
            return settings;
        }

        private static void Validate(DxMessagingRuntimeSettings settings)
        {
            ValidateMethod.Invoke(settings, null);
        }

        private static void Process()
        {
            ProcessMethod.Invoke(null, null);
        }
    }
}
#endif
