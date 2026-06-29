#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
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

            AssertOverDeregistrationLogged(
                handler,
                bus =>
                {
                    MessageBusRegistration reg = bus.RegisterGlobalAcceptAll(handler);
                    // Over-deregister should log an error (once is valid, twice is over-deregistration).
                    bus.Deregister<IMessage>(in reg);
                    bus.Deregister<IMessage>(in reg);
                },
                "GlobalAcceptAll"
            );
        }

        /// <summary>
        /// Bus-side over-deregistration logging for the scalar (untargeted) and keyed
        /// (targeted / sourced-broadcast) handler deregistration paths -- the second
        /// <see cref="IMessageBus.Deregister{T}"/> for the same handle is a genuine
        /// over-deregistration and must log an error. Parameterized across kinds so the
        /// method-&gt;sink reverse mapping is exercised for every handler registration method.
        /// </summary>
        [Test]
        public void HandlerOverDeregistrationLogsError(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject go = new($"{nameof(HandlerOverDeregistrationLogsError)}_{scenario.Kind}");
            _spawned.Add(go);
            MessageHandler handler = new(go) { active = true };
            InstanceId context = go;

            AssertOverDeregistrationLogged(
                handler,
                bus =>
                {
                    switch (scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                        {
                            MessageBusRegistration reg =
                                bus.RegisterUntargeted<SimpleUntargetedMessage>(handler);
                            bus.Deregister<SimpleUntargetedMessage>(in reg);
                            bus.Deregister<SimpleUntargetedMessage>(in reg);
                            break;
                        }
                        case MessageKind.Targeted:
                        {
                            MessageBusRegistration reg =
                                bus.RegisterTargeted<SimpleTargetedMessage>(context, handler);
                            bus.Deregister<SimpleTargetedMessage>(in reg);
                            bus.Deregister<SimpleTargetedMessage>(in reg);
                            break;
                        }
                        case MessageKind.Broadcast:
                        {
                            MessageBusRegistration reg =
                                bus.RegisterSourcedBroadcast<SimpleBroadcastMessage>(
                                    context,
                                    handler
                                );
                            bus.Deregister<SimpleBroadcastMessage>(in reg);
                            bus.Deregister<SimpleBroadcastMessage>(in reg);
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException(
                                nameof(scenario),
                                scenario.Kind,
                                "Unsupported message kind."
                            );
                    }
                },
                $"{scenario.Kind} handler"
            );
        }

        /// <summary>
        /// Bus-side over-deregistration logging for the post-processor deregistration paths
        /// (scalar untargeted + keyed targeted / broadcast), exercising the post-processor
        /// entries of the method-&gt;sink reverse table.
        /// </summary>
        [Test]
        public void PostProcessorOverDeregistrationLogsError(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject go = new(
                $"{nameof(PostProcessorOverDeregistrationLogsError)}_{scenario.Kind}"
            );
            _spawned.Add(go);
            MessageHandler handler = new(go) { active = true };
            InstanceId context = go;

            AssertOverDeregistrationLogged(
                handler,
                bus =>
                {
                    switch (scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                        {
                            MessageBusRegistration reg =
                                bus.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                                    handler
                                );
                            bus.Deregister<SimpleUntargetedMessage>(in reg);
                            bus.Deregister<SimpleUntargetedMessage>(in reg);
                            break;
                        }
                        case MessageKind.Targeted:
                        {
                            MessageBusRegistration reg =
                                bus.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                                    context,
                                    handler
                                );
                            bus.Deregister<SimpleTargetedMessage>(in reg);
                            bus.Deregister<SimpleTargetedMessage>(in reg);
                            break;
                        }
                        case MessageKind.Broadcast:
                        {
                            MessageBusRegistration reg =
                                bus.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                                    context,
                                    handler
                                );
                            bus.Deregister<SimpleBroadcastMessage>(in reg);
                            bus.Deregister<SimpleBroadcastMessage>(in reg);
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException(
                                nameof(scenario),
                                scenario.Kind,
                                "Unsupported message kind."
                            );
                    }
                },
                $"{scenario.Kind} post-processor"
            );
        }

        /// <summary>
        /// Bus-side over-deregistration logging for the three interceptor stores (the store is
        /// picked by the handle's kind, since all three interceptor registrars log the single
        /// <see cref="RegistrationMethod.Interceptor"/>).
        /// </summary>
        [Test]
        public void InterceptorOverDeregistrationLogsError(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            AssertOverDeregistrationLogged(
                handler: null,
                act: bus =>
                {
                    switch (scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                        {
                            MessageBusRegistration reg = bus.RegisterUntargetedInterceptor(
                                (ref SimpleUntargetedMessage _) => true
                            );
                            bus.Deregister<SimpleUntargetedMessage>(in reg);
                            bus.Deregister<SimpleUntargetedMessage>(in reg);
                            break;
                        }
                        case MessageKind.Targeted:
                        {
                            MessageBusRegistration reg = bus.RegisterTargetedInterceptor(
                                (ref InstanceId _, ref SimpleTargetedMessage _) => true
                            );
                            bus.Deregister<SimpleTargetedMessage>(in reg);
                            bus.Deregister<SimpleTargetedMessage>(in reg);
                            break;
                        }
                        case MessageKind.Broadcast:
                        {
                            MessageBusRegistration reg = bus.RegisterBroadcastInterceptor(
                                (ref InstanceId _, ref SimpleBroadcastMessage _) => true
                            );
                            bus.Deregister<SimpleBroadcastMessage>(in reg);
                            bus.Deregister<SimpleBroadcastMessage>(in reg);
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException(
                                nameof(scenario),
                                scenario.Kind,
                                "Unsupported message kind."
                            );
                    }
                },
                label: $"{scenario.Kind} interceptor"
            );
        }

        private static void AssertOverDeregistrationLogged(
            MessageHandler handler,
            Action<IMessageBus> act,
            string label
        )
        {
            List<string> logs = new();
            Action<LogLevel, string> previous = MessagingDebug.LogFunction;
            try
            {
                MessagingDebug.LogFunction = (level, msg) => logs.Add($"{level}:{msg}");
                act(MessageHandler.MessageBus);

                bool saw = logs.Exists(l =>
                    l.Contains("Error:") && l.Contains("over-deregistration")
                );
                Assert.IsTrue(
                    saw,
                    $"Expected an error log indicating over-deregistration for {label}. Got: "
                        + string.Join(" | ", logs)
                );
            }
            finally
            {
                MessagingDebug.LogFunction = previous;
                if (handler != null)
                {
                    handler.active = false;
                }
            }
        }
    }
}

#endif
