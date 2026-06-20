#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class ReflexiveMessageWarningTests : MessagingTestBase
    {
        [Test]
        public void LogsWarningOncePerBus()
        {
            List<(LogLevel level, string message)> logs = new();
            Action<LogLevel, string> previousLogFunction = MessagingDebug.LogFunction;
            try
            {
                MessagingDebug.LogFunction = (level, message) => logs.Add((level, message));
                MessagingDebug.enabled = true;
                logs.Clear();

                GameObject go = new("ReflexiveReceiver", typeof(ReflexiveReceiverComponent));
                _spawned.Add(go);
                ReflexiveReceiverComponent receiver = go.GetComponent<ReflexiveReceiverComponent>();

                MessageBus bus = new();
                ReflexiveMessage message = new("OnReflexive", ReflexiveSendMode.Flat);

                int warningsBefore = CountWarnings(logs);
                InstanceId target = receiver;
                bus.TargetedBroadcast(ref target, ref message);
                Assert.AreEqual(1, receiver.InvocationCount);
                int warningsAfter = CountWarnings(logs);
                Assert.Greater(
                    warningsAfter,
                    warningsBefore,
                    "First reflexive dispatch should log a warning."
                );
                StringAssert.Contains("ReflexiveMessage", logs[^1].message);

                warningsBefore = warningsAfter;
                target = receiver;
                bus.TargetedBroadcast(ref target, ref message);
                Assert.AreEqual(2, receiver.InvocationCount);
                warningsAfter = CountWarnings(logs);
                Assert.AreEqual(
                    warningsBefore,
                    warningsAfter,
                    "Second dispatch on the same bus should not emit additional warnings."
                );

                MessageBus secondBus = new();
                warningsBefore = warningsAfter;
                target = receiver;
                secondBus.TargetedBroadcast(ref target, ref message);
                Assert.AreEqual(3, receiver.InvocationCount);
                warningsAfter = CountWarnings(logs);
                Assert.AreEqual(
                    warningsBefore + 1,
                    warningsAfter,
                    "A new bus should emit its own warning."
                );
            }
            finally
            {
                MessagingDebug.LogFunction = previousLogFunction;
            }
        }

        private static int CountWarnings(List<(LogLevel level, string message)> logs)
        {
            return logs.Count(entry =>
                entry.level == LogLevel.Warn
                && entry.message.IndexOf("ReflexiveMessage dispatch", StringComparison.Ordinal) >= 0
            );
        }
    }
}

#endif
