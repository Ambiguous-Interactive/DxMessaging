#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class LifecycleTests : MessagingTestBase
    {
        [UnityTest]
        public IEnumerator MessageAwareComponentStopsReceivingAfterDestroy()
        {
            GameObject host = new(nameof(MessageAwareComponentStopsReceivingAfterDestroy), typeof(SimpleMessageAwareComponent));
            _spawned.Add(host);
            SimpleMessageAwareComponent component = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            component.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            Object.Destroy(component);
            yield return null;

            message.EmitUntargeted();
            Assert.AreEqual(1, count);
        }

        [UnityTest]
        public IEnumerator MessageAwareComponentRespectsApplicationQuit()
        {
            GameObject host = new(nameof(MessageAwareComponentRespectsApplicationQuit), typeof(SimpleMessageAwareComponent));
            _spawned.Add(host);
            SimpleMessageAwareComponent component = host.GetComponent<SimpleMessageAwareComponent>();

            MessageRegistrationToken token = GetToken(component);
            Assert.IsTrue(token.Enabled);

            host.SendMessage("OnApplicationQuit");
            component.enabled = false;
            yield return null;
            component.enabled = true;
            yield return null;

            Assert.IsTrue(token.Enabled);

            int count = 0;
            component.untargetedHandler = () => ++count;
            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Token should remain enabled even when the component cycles after application quit.");
        }
    }
}

#endif
