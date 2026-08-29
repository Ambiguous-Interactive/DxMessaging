#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using NUnit.Framework;
    using Scripts.Components;
    using Scripts.Messages;
    using UnityEngine;

    public sealed class GenericMessageTests : MessagingTestBase
    {
        [Test]
        public void MultipleGenericTypesWork()
        {
            GameObject go = new(
                nameof(MultipleGenericTypesWork),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(go);

            SimpleMessageAwareComponent messaging = go.GetComponent<SimpleMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(messaging);

            int totalCount = 0;
            token.RegisterUntargeted((in GenericUntargetedMessage<int> _) => totalCount++);
            token.RegisterUntargeted((in GenericUntargetedMessage<float> _) => totalCount++);
            token.RegisterUntargeted((in GenericUntargetedMessage<string> _) => totalCount++);
            token.RegisterUntargeted((in GenericUntargetedMessage<Vector3> _) => totalCount++);

            GenericUntargetedMessage<int> intMessage = new();
            intMessage.EmitUntargeted();
            Assert.AreEqual(1, totalCount);
            GenericUntargetedMessage<float> floatMessage = new();
            floatMessage.EmitUntargeted();
            Assert.AreEqual(2, totalCount);
            GenericUntargetedMessage<string> stringMessage = new();
            stringMessage.EmitUntargeted();
            Assert.AreEqual(3, totalCount);
            GenericUntargetedMessage<Vector3> vector3Message = new();
            vector3Message.EmitUntargeted();
            Assert.AreEqual(4, totalCount);
            GenericUntargetedMessage<Vector4> vector4Message = new();
            vector4Message.EmitUntargeted();
            Assert.AreEqual(4, totalCount);
        }
    }
}

#endif
