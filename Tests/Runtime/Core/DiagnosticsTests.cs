#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Core.DataStructure;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class DiagnosticsTests : MessagingTestBase
    {
        [Test]
        public void TokenDiagnosticModeTracksEmissions()
        {
            GameObject host = new(
                nameof(TokenDiagnosticModeTracksEmissions),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            token.DiagnosticMode = true;

            int count = 0;
            MessageRegistrationHandle handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                _ => ++count
            );

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            Dictionary<MessageRegistrationHandle, int> callCounts = GetCallCounts(token);
            Assert.IsTrue(callCounts.TryGetValue(handle, out int recordedCount));
            Assert.AreEqual(1, recordedCount);

            CyclicBuffer<MessageEmissionData> emissions = GetEmissionBuffer(token);
            Assert.AreEqual(1, emissions.Count);

            token.RemoveRegistration(handle);
        }

        [Test]
        public void TokenDiagnosticModeStampsTraceIdsAndRegistrationHandles()
        {
            GameObject host = new(nameof(TokenDiagnosticModeStampsTraceIdsAndRegistrationHandles));
            _spawned.Add(host);
            MessageHandler handler = new(host) { active = true };
            MessageBus customBus = new() { DiagnosticsMode = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, customBus);
            token.DiagnosticMode = true;
            token.Enable();

            int count = 0;
            MessageRegistrationHandle handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                _ => ++count
            );

            SimpleUntargetedMessage message = new();
            customBus.UntargetedBroadcast(ref message);

            CyclicBuffer<MessageEmissionData> busEmissions = GetEmissionBuffer(customBus);
            CyclicBuffer<MessageEmissionData> tokenEmissions = GetEmissionBuffer(token);

            Assert.AreEqual(1, count);
            Assert.AreEqual(1, busEmissions.Count);
            Assert.AreEqual(1, tokenEmissions.Count);
            Assert.Greater(
                busEmissions[0].traceId,
                0,
                "Bus-side diagnostics must stamp the dispatch trace id."
            );
            Assert.AreEqual(
                busEmissions[0].traceId,
                tokenEmissions[0].traceId,
                "Token-side diagnostics must use the same trace id as the bus emission."
            );
            Assert.AreEqual(
                default(MessageRegistrationHandle),
                busEmissions[0].registrationHandle,
                "Bus-side diagnostics are emission records, not delivery records."
            );
            Assert.AreEqual(
                handle,
                tokenEmissions[0].registrationHandle,
                "Token-side diagnostics must identify the delivered registration."
            );

            token.Dispose();
            handler.active = false;
        }

        [Test]
        public void TokenDiagnosticModeLeavesTraceIdZeroOutsideBusDispatch()
        {
            GameObject host = new(nameof(TokenDiagnosticModeLeavesTraceIdZeroOutsideBusDispatch));
            _spawned.Add(host);
            MessageBus customBus = new() { DiagnosticsMode = true };
            MessageHandler handler = new(host, customBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, customBus);
            token.DiagnosticMode = true;
            token.Enable();

            int count = 0;
            MessageRegistrationHandle handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                _ => ++count
            );

            SimpleUntargetedMessage message = new();
            handler.HandleUntargetedMessage(ref message, customBus, priority: 0);

            CyclicBuffer<MessageEmissionData> busEmissions = GetEmissionBuffer(customBus);
            CyclicBuffer<MessageEmissionData> tokenEmissions = GetEmissionBuffer(token);

            Assert.AreEqual(1, count);
            Assert.AreEqual(
                0,
                busEmissions.Count,
                "Direct handler dispatch is not a bus emission and must not create a bus trace record."
            );
            Assert.AreEqual(1, tokenEmissions.Count);
            Assert.AreEqual(
                0,
                tokenEmissions[0].traceId,
                "Token diagnostics outside a concrete bus dispatch must not reuse a stale emission id."
            );
            Assert.AreEqual(handle, tokenEmissions[0].registrationHandle);

            token.Dispose();
            handler.active = false;
        }

        [Test]
        public void TokenTraceIdUsesLiveRegistrationBusAfterPreserveRetarget()
        {
            GameObject host = new(nameof(TokenTraceIdUsesLiveRegistrationBusAfterPreserveRetarget));
            _spawned.Add(host);
            MessageBus originalBus = new() { DiagnosticsMode = true };
            MessageBus newBus = new() { DiagnosticsMode = true };
            MessageHandler handler = new(host, originalBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, originalBus);
            token.DiagnosticMode = true;
            token.Enable();

            int count = 0;
            MessageRegistrationHandle handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                _ => ++count
            );

            token.RetargetMessageBus(newBus, MessageBusRebindMode.PreserveRegistrations);

            SimpleUntargetedMessage message = new();
            originalBus.UntargetedBroadcast(ref message);

            CyclicBuffer<MessageEmissionData> originalBusEmissions = GetEmissionBuffer(originalBus);
            CyclicBuffer<MessageEmissionData> newBusEmissions = GetEmissionBuffer(newBus);
            CyclicBuffer<MessageEmissionData> tokenEmissions = GetEmissionBuffer(token);

            Assert.AreEqual(1, count);
            Assert.AreEqual(1, originalBusEmissions.Count);
            Assert.AreEqual(0, newBusEmissions.Count);
            Assert.AreEqual(1, tokenEmissions.Count);
            Assert.AreEqual(
                originalBusEmissions[0].traceId,
                tokenEmissions[0].traceId,
                "Preserved registrations must trace against the bus that still owns the live delegate."
            );
            Assert.AreEqual(handle, tokenEmissions[0].registrationHandle);

            token.Dispose();
            handler.active = false;
        }

        [Test]
        public void ActionRegistrationDiagnosticsRecordedThroughFlatBusDispatch(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            RunActionRegistrationDiagnosticsScenario(
                scenario,
                legacyHandlerDispatch: false,
                nameof(ActionRegistrationDiagnosticsRecordedThroughFlatBusDispatch)
            );
        }

        [Test]
        public void ActionRegistrationDiagnosticsRecordedThroughLegacyHandlerDispatch(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            RunActionRegistrationDiagnosticsScenario(
                scenario,
                legacyHandlerDispatch: true,
                nameof(ActionRegistrationDiagnosticsRecordedThroughLegacyHandlerDispatch)
            );
        }

        [Test]
        public void MessageBusDiagnosticsRespectBufferSize()
        {
            using (new DiagnosticsScope(DiagnosticsTarget.All, messageBufferSize: 2))
            {
                GameObject host = new(nameof(MessageBusDiagnosticsRespectBufferSize));
                _spawned.Add(host);
                MessageHandler handler = new(host) { active = true };
                MessageBus customBus = new() { DiagnosticsMode = true };

                MessageRegistrationToken token = MessageRegistrationToken.Create(
                    handler,
                    customBus
                );
                token.DiagnosticMode = true;
                token.Enable();

                int count = 0;
                MessageRegistrationHandle handle =
                    token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count);

                SimpleUntargetedMessage message = new();
                for (int i = 0; i < 3; ++i)
                {
                    message.EmitUntargeted(customBus);
                }
                Assert.AreEqual(3, count);

                CyclicBuffer<MessageEmissionData> busBuffer = GetEmissionBuffer(customBus);
                Assert.AreEqual(2, busBuffer.Count);

                token.RemoveRegistration(handle);
                token.Disable();
                handler.active = false;
            }
        }

        private static Dictionary<MessageRegistrationHandle, int> GetCallCounts(
            MessageRegistrationToken token
        )
        {
            return token._callCounts;
        }

        private static CyclicBuffer<MessageEmissionData> GetEmissionBuffer(
            MessageRegistrationToken token
        )
        {
            return token._emissionBuffer;
        }

        private static CyclicBuffer<MessageEmissionData> GetEmissionBuffer(MessageBus bus)
        {
            return bus._emissionBuffer;
        }

        private void RunActionRegistrationDiagnosticsScenario(
            MessageScenario scenario,
            bool legacyHandlerDispatch,
            string testName
        )
        {
            MessageBus bus = new() { DiagnosticsMode = true };
            GameObject host = new(testName + "_" + scenario.DisplayName);
            _spawned.Add(host);
            MessageHandler handler = new(host, bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.DiagnosticMode = true;
            token.Enable();

            using LeakWatcher watcher = new(bus, label: scenario.DisplayName);
            int userCallCount = 0;
            MessageRegistrationHandle handle = RegisterActionHandler(
                scenario,
                token,
                handler.owner,
                () => ++userCallCount
            );

            try
            {
                if (legacyHandlerDispatch)
                {
                    EmitThroughLegacyHandler(scenario, handler, bus, handler.owner);
                }
                else
                {
                    EmitThroughBus(scenario, bus, handler.owner);
                }

                Assert.AreEqual(
                    1,
                    userCallCount,
                    "[{0}] User Action handler must run exactly once.",
                    scenario.DisplayName
                );

                Dictionary<MessageRegistrationHandle, int> callCounts = GetCallCounts(token);
                Assert.IsTrue(
                    callCounts.TryGetValue(handle, out int recordedCount),
                    "[{0}] Diagnostics call-count entry missing for handle {1}.",
                    scenario.DisplayName,
                    handle
                );
                Assert.AreEqual(
                    1,
                    recordedCount,
                    "[{0}] Diagnostics call count must match the delivered Action handler.",
                    scenario.DisplayName
                );

                CyclicBuffer<MessageEmissionData> emissions = GetEmissionBuffer(token);
                Assert.AreEqual(
                    1,
                    emissions.Count,
                    "[{0}] Diagnostics emission history must record the delivered Action handler.",
                    scenario.DisplayName
                );
            }
            finally
            {
                token.RemoveRegistration(handle);
                token.Disable();
                handler.active = false;
            }
        }

        private static MessageRegistrationHandle RegisterActionHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onCall
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (SimpleUntargetedMessage _) => onCall()
                    );
                case MessageKind.Targeted:
                    return token.RegisterTargeted<SimpleTargetedMessage>(
                        context,
                        (SimpleTargetedMessage _) => onCall()
                    );
                case MessageKind.Broadcast:
                    return token.RegisterBroadcast<SimpleBroadcastMessage>(
                        context,
                        (SimpleBroadcastMessage _) => onCall()
                    );
                case MessageKind.TargetedWithoutTargeting:
                    return token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        (InstanceId _, SimpleTargetedMessage _) => onCall()
                    );
                case MessageKind.BroadcastWithoutSource:
                    return token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        (InstanceId _, SimpleBroadcastMessage _) => onCall()
                    );
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario.Kind, null);
            }
        }

        private static void EmitThroughBus(
            MessageScenario scenario,
            MessageBus bus,
            InstanceId context
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    bus.UntargetedBroadcast(ref message);
                    return;
                }
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                {
                    SimpleTargetedMessage message = new();
                    InstanceId target = context;
                    bus.TargetedBroadcast(ref target, ref message);
                    return;
                }
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                {
                    SimpleBroadcastMessage message = new();
                    InstanceId source = context;
                    bus.SourcedBroadcast(ref source, ref message);
                    return;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario.Kind, null);
            }
        }

        private static void EmitThroughLegacyHandler(
            MessageScenario scenario,
            MessageHandler handler,
            MessageBus bus,
            InstanceId context
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    handler.HandleUntargetedMessage(ref message, bus, priority: 0);
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    InstanceId target = context;
                    handler.HandleTargeted(ref target, ref message, bus, priority: 0);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    InstanceId source = context;
                    handler.HandleSourcedBroadcast(ref source, ref message, bus, priority: 0);
                    return;
                }
                case MessageKind.TargetedWithoutTargeting:
                {
                    SimpleTargetedMessage message = new();
                    InstanceId target = context;
                    handler.HandleTargetedWithoutTargeting(
                        ref target,
                        ref message,
                        bus,
                        priority: 0
                    );
                    return;
                }
                case MessageKind.BroadcastWithoutSource:
                {
                    SimpleBroadcastMessage message = new();
                    InstanceId source = context;
                    handler.HandleSourcedBroadcastWithoutSource(
                        ref source,
                        ref message,
                        bus,
                        priority: 0
                    );
                    return;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario.Kind, null);
            }
        }

        [Test]
        public void GlobalMessageBufferSizeDefaultMatchesConstant()
        {
            // Verify the default GlobalMessageBufferSize matches the DefaultMessageBufferSize constant
            // to ensure consistency between the static property and the constant.
            Assert.AreEqual(
                IMessageBus.DefaultMessageBufferSize,
                100,
                "DefaultMessageBufferSize constant should be 100."
            );
        }

        [Test]
        public void RuntimeSettingsMessageBufferSizeResizesExistingAndNewBuses()
        {
            using DiagnosticsScope bufferScope = new();
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            IDisposable overrideToken = null;
            try
            {
                MessageBus existingBus = MessageBus.CreateForInternalUse(new FakeClock());
                settings._messageBufferSize = 2;
                overrideToken = DxMessagingRuntimeSettingsProvider.Override(settings);

                Assert.AreEqual(2, IMessageBus.GlobalMessageBufferSize);
                Assert.AreEqual(2, GetEmissionBuffer(existingBus).Capacity);

                MessageBus newBus = MessageBus.CreateForInternalUse(new FakeClock());
                Assert.AreEqual(2, GetEmissionBuffer(newBus).Capacity);

                settings._messageBufferSize = 1;
                DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);

                Assert.AreEqual(1, IMessageBus.GlobalMessageBufferSize);
                Assert.AreEqual(1, GetEmissionBuffer(existingBus).Capacity);
                Assert.AreEqual(1, GetEmissionBuffer(newBus).Capacity);
            }
            finally
            {
                overrideToken?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ZeroBufferSizeDiscardsEmissions()
        {
            using (new DiagnosticsScope(DiagnosticsTarget.All, messageBufferSize: 0))
            {
                GameObject host = new(nameof(ZeroBufferSizeDiscardsEmissions));
                _spawned.Add(host);
                MessageHandler handler = new(host) { active = true };
                MessageBus customBus = new() { DiagnosticsMode = true };

                MessageRegistrationToken token = MessageRegistrationToken.Create(
                    handler,
                    customBus
                );
                token.DiagnosticMode = true;
                token.Enable();

                int count = 0;
                MessageRegistrationHandle handle =
                    token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count);

                SimpleUntargetedMessage message = new();
                for (int i = 0; i < 5; ++i)
                {
                    message.EmitUntargeted(customBus);
                }

                // Messages should still be delivered to handlers
                Assert.AreEqual(
                    5,
                    count,
                    "Messages should still be delivered even with zero buffer size."
                );

                // But the emission buffer should remain empty (emissions are silently discarded)
                CyclicBuffer<MessageEmissionData> busBuffer = GetEmissionBuffer(customBus);
                Assert.AreEqual(
                    0,
                    busBuffer.Count,
                    "Zero buffer size should discard all emissions."
                );

                token.RemoveRegistration(handle);
                token.Disable();
                handler.active = false;
            }
        }
    }
}

#endif
