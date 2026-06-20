#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OverDeregistrationTests : MessagingTestBase
    {
        [Test]
        public void MessageBusGlobalAcceptAllOverDeregistrationLogsError()
        {
            GameObject go = new(nameof(MessageBusGlobalAcceptAllOverDeregistrationLogsError));
            _spawned.Add(go);
            MessageHandler handler = new(go) { active = true };

            List<string> logs = new();
            Action<LogLevel, string> previous = MessagingDebug.LogFunction;
            try
            {
                MessagingDebug.LogFunction = (level, msg) => logs.Add($"{level}:{msg}");

                IMessageBus bus = MessageHandler.MessageBus;
                Action dereg = bus.RegisterGlobalAcceptAll(handler);

                // Over-deregister should log an error (once is valid, twice is over-deregistration)
                dereg();
                dereg();

                bool saw = logs.Exists(l =>
                    l.Contains("Error:") && l.Contains("over-deregistration")
                );
                Assert.IsTrue(
                    saw,
                    "Expected an error log indicating over-deregistration for GlobalAcceptAll. Got: "
                        + string.Join(" | ", logs)
                );
            }
            finally
            {
                MessagingDebug.LogFunction = previous;
                handler.active = false;
            }
        }

        [Test]
        public void InterceptorOverDeregistrationLogsError()
        {
            List<string> logs = new();
            Action<LogLevel, string> previous = MessagingDebug.LogFunction;
            try
            {
                MessagingDebug.LogFunction = (level, msg) => logs.Add($"{level}:{msg}");

                IMessageBus bus = MessageHandler.MessageBus;
                Action dereg = bus.RegisterUntargetedInterceptor(
                    (ref SimpleUntargetedMessage _) => true
                );

                dereg();
                dereg();

                bool saw = logs.Exists(l =>
                    l.Contains("Error:") && l.Contains("over-deregistration")
                );
                Assert.IsTrue(
                    saw,
                    "Expected an error log indicating over-deregistration for interceptor removal. Got: "
                        + string.Join(" | ", logs)
                );
            }
            finally
            {
                MessagingDebug.LogFunction = previous;
            }
        }

        [Test]
        public void UntargetedHandlerOverDeregistrationLogsError()
        {
            GameObject go = new(nameof(UntargetedHandlerOverDeregistrationLogsError));
            _spawned.Add(go);
            MessageHandler handler = new(go) { active = true };

            List<string> logs = new();
            Action<LogLevel, string> previous = MessagingDebug.LogFunction;
            try
            {
                MessagingDebug.LogFunction = (level, msg) => logs.Add($"{level}:{msg}");

                // Register via bus to ensure we exercise the bus-side over-deregistration logging path
                IMessageBus bus = MessageHandler.MessageBus;
                Action dereg = bus.RegisterUntargeted<SimpleUntargetedMessage>(handler);

                dereg();
                dereg();

                bool saw = logs.Exists(l =>
                    l.Contains("Error:") && l.Contains("over-deregistration")
                );
                Assert.IsTrue(
                    saw,
                    "Expected an error log indicating over-deregistration for untargeted handler removal. Got: "
                        + string.Join(" | ", logs)
                );
            }
            finally
            {
                MessagingDebug.LogFunction = previous;
                handler.active = false;
            }
        }
    }
}

#endif
