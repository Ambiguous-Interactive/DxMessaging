#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Core.DataStructure;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class DiagnosticsTests : MessagingTestBase
    {
        [UnityTest]
        public IEnumerator TokenDiagnosticModeTracksEmissions()
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
            yield break;
        }

        [UnityTest]
        public IEnumerator MessageBusDiagnosticsRespectBufferSize()
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

            yield break;
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

        [UnityTest]
        public IEnumerator ZeroBufferSizeDiscardsEmissions()
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

            yield break;
        }
    }
}

#endif
