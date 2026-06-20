#if UNITY_2021_3_OR_NEWER
// ReSharper disable AccessToModifiedClosure
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class UntargetedEquivalenceTests : MessagingTestBase
    {
        [Test]
        public void StructEmitEqualsEmitUntargeted()
        {
            GameObject go = new(
                nameof(StructEmitEqualsEmitUntargeted),
                typeof(UntargetedReceiverComponent)
            );
            _spawned.Add(go);
            UntargetedReceiverComponent comp = go.GetComponent<UntargetedReceiverComponent>();

            SimpleUntargetedMessage m = new();
            m.Emit();
            Assert.AreEqual(1, comp.count);

            m.EmitUntargeted();
            Assert.AreEqual(2, comp.count);
        }

        [Test]
        public void StructEmitAndEmitUntargetedRespectInterceptors()
        {
            GameObject go = new(
                nameof(StructEmitAndEmitUntargetedRespectInterceptors),
                typeof(UntargetedReceiverComponent)
            );
            _spawned.Add(go);
            UntargetedReceiverComponent comp = go.GetComponent<UntargetedReceiverComponent>();

            IMessageBus bus = MessageHandler.MessageBus;
            bool cancel = true;
            Action dereg = bus.RegisterUntargetedInterceptor(
                (ref SimpleUntargetedMessage _) => !cancel
            );
            try
            {
                SimpleUntargetedMessage m = new();
                m.Emit(); // cancelled
                m.EmitUntargeted(); // cancelled
                Assert.AreEqual(0, comp.count);

                cancel = false;
                m.Emit();
                m.EmitUntargeted();
                Assert.AreEqual(2, comp.count);
            }
            finally
            {
                dereg();
            }
        }

        [Test]
        public void StructExplicitBusParameterRoutesCorrectly()
        {
            GameObject globalGo = new(
                nameof(StructExplicitBusParameterRoutesCorrectly) + "_Global",
                typeof(UntargetedReceiverComponent)
            );
            _spawned.Add(globalGo);
            UntargetedReceiverComponent globalComp =
                globalGo.GetComponent<UntargetedReceiverComponent>();

            GameObject customGo = new(
                nameof(StructExplicitBusParameterRoutesCorrectly) + "_Custom"
            );
            _spawned.Add(customGo);
            MessageHandler customHandler = new(customGo) { active = true };
            MessageBus customBus = new();
            MessageRegistrationToken customToken = MessageRegistrationToken.Create(
                customHandler,
                customBus
            );
            customToken.Enable();

            int customCount = 0;
            MessageRegistrationHandle customHandle =
                customToken.RegisterUntargeted<SimpleUntargetedMessage>(_ => customCount++);

            SimpleUntargetedMessage m = new();
            m.Emit(customBus);
            m.EmitUntargeted(customBus);
            Assert.AreEqual(0, globalComp.count);
            Assert.AreEqual(2, customCount);

            // Default emission should hit global
            m.Emit();
            Assert.AreEqual(1, globalComp.count);

            customToken.RemoveRegistration(customHandle);
            customHandler.active = false;
        }

        [Test]
        public void ClassEmitEqualsEmitUntargeted()
        {
            GameObject go = new(
                nameof(ClassEmitEqualsEmitUntargeted),
                typeof(UntargetedClassReceiverComponent)
            );
            _spawned.Add(go);
            UntargetedClassReceiverComponent comp =
                go.GetComponent<UntargetedClassReceiverComponent>();

            ClassUntargetedMessage m = new("hello");
            m.Emit();
            Assert.AreEqual(1, comp.count);
            m.EmitUntargeted();
            Assert.AreEqual(2, comp.count);
        }

        [Test]
        public void ClassExplicitBusParameterRoutesCorrectly()
        {
            GameObject globalGo = new(
                nameof(ClassExplicitBusParameterRoutesCorrectly) + "_Global",
                typeof(UntargetedClassReceiverComponent)
            );
            _spawned.Add(globalGo);
            UntargetedClassReceiverComponent globalComp =
                globalGo.GetComponent<UntargetedClassReceiverComponent>();

            GameObject customGo = new(nameof(ClassExplicitBusParameterRoutesCorrectly) + "_Custom");
            _spawned.Add(customGo);
            MessageHandler customHandler = new(customGo) { active = true };
            MessageBus customBus = new();
            MessageRegistrationToken customToken = MessageRegistrationToken.Create(
                customHandler,
                customBus
            );
            customToken.Enable();

            int customCount = 0;
            MessageRegistrationHandle customHandle =
                customToken.RegisterUntargeted<ClassUntargetedMessage>(_ => customCount++);

            ClassUntargetedMessage m = new("x");
            m.Emit(customBus);
            m.EmitUntargeted(customBus);
            Assert.AreEqual(0, globalComp.count);
            Assert.AreEqual(2, customCount);

            m.Emit();
            Assert.AreEqual(1, globalComp.count);

            customToken.RemoveRegistration(customHandle);
            customHandler.active = false;
        }
    }
}

#endif
