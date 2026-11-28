#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class MessagingComponentLifecycleTests : MessagingTestBase
    {
        [UnityTest]
        public IEnumerator ReleasesListenerOnDestroy()
        {
            GameObject go = new("Lifecycle", typeof(MessagingComponent), typeof(SimpleMessageAwareComponent));
            _spawned.Add(go);

            MessagingComponent messaging = go.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent listener = go.GetComponent<SimpleMessageAwareComponent>();

            yield return null;

            Assert.AreEqual(1, messaging._registeredListeners.Count, "Expected initial listener registration.");

            Object.Destroy(listener);
            yield return null;

            Assert.AreEqual(0, messaging._registeredListeners.Count, "Listener dictionary should be cleared after destroy.");

            SimpleMessageAwareComponent replacement = go.AddComponent<SimpleMessageAwareComponent>();
            yield return null;

            Assert.AreEqual(1, messaging._registeredListeners.Count, "Replacement listener should be tracked.");
            Assert.IsTrue(messaging._registeredListeners.ContainsKey(replacement));
        }

        [UnityTest]
        public IEnumerator ManualReleaseRemovesListenerAndDisablesToken()
        {
            GameObject go = new("ManualRelease", typeof(MessagingComponent), typeof(ManualListenerComponent));
            _spawned.Add(go);

            MessagingComponent messaging = go.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = go.GetComponent<ManualListenerComponent>();

            MessageRegistrationToken token = listener.RequestToken(messaging);
            Assert.AreEqual(1, messaging._registeredListeners.Count, "Token request should register listener.");

            token.Enable();
            Assert.IsTrue(token.Enabled, "Token should enable successfully.");

            messaging.Release(listener);
            yield return null;

            Assert.AreEqual(0, messaging._registeredListeners.Count, "Manual release should remove listener.");
            Assert.IsFalse(token.Enabled, "Released token should be disabled.");
        }
    }
}

#endif
