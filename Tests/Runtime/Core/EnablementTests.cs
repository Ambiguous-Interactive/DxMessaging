#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections;
    using DxMessaging.Core.Extensions;
    using NUnit.Framework;
    using Scripts.Components;
    using Scripts.Messages;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class EnablementTests : MessagingTestBase
    {
        [Test]
        public void StartsDisabled()
        {
            GameObject prefab = new("Prefab", typeof(SimpleMessageAwareComponent));
            _spawned.Add(prefab);
            SimpleMessageAwareComponent prefabMessaging =
                prefab.GetComponent<SimpleMessageAwareComponent>();
            prefabMessaging.enabled = false;

            GameObject copy = Object.Instantiate(prefab);
            _spawned.Add(copy);
            SimpleMessageAwareComponent spawnedMessaging =
                copy.GetComponent<SimpleMessageAwareComponent>();
            Assert.IsFalse(spawnedMessaging.enabled);
            int copyCount = 0;
            spawnedMessaging.untargetedHandler += () => copyCount++;

            SimpleUntargetedMessage untargeted = new();
            untargeted.EmitUntargeted();
            Assert.AreEqual(0, copyCount);

            spawnedMessaging.enabled = true;
            untargeted.EmitUntargeted();
            Assert.AreEqual(1, copyCount);
        }

        [UnityTest]
        public IEnumerator StartsEnabled()
        {
            GameObject prefab = new("Prefab", typeof(SimpleMessageAwareComponent));
            _spawned.Add(prefab);
            SimpleMessageAwareComponent prefabMessaging =
                prefab.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            prefabMessaging.untargetedHandler += () => count++;

            SimpleUntargetedMessage untargeted = new();
            untargeted.EmitUntargeted();
            Assert.AreEqual(1, count);

            prefabMessaging.enabled = false;
            untargeted.EmitUntargeted();
            Assert.AreEqual(1, count);

            prefabMessaging.enabled = true;
            untargeted.EmitUntargeted();
            Assert.AreEqual(2, count);

            Object.Destroy(prefabMessaging);
            yield return null;
            untargeted.EmitUntargeted();
            Assert.AreEqual(2, count);
        }
    }
}

#endif
