#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class StringShorthandTests : MessagingTestBase
    {
        [Test]
        public void EmitAndEmitAtTargeting()
        {
            GameObject go = new(
                nameof(EmitAndEmitAtTargeting),
                typeof(StringMessageAwareComponent)
            );
            _spawned.Add(go);
            StringMessageAwareComponent comp = go.GetComponent<StringMessageAwareComponent>();

            Assert.AreEqual(0, comp.gameObjectTargetedCount);
            Assert.AreEqual(0, comp.componentTargetedCount);
            Assert.AreEqual(0, comp.targetedWithoutTargetingCount);

            // Target the GameObject (GO-based listeners should receive)
            "Hello".EmitAt((InstanceId)go);
            Assert.AreEqual(1, comp.gameObjectTargetedCount);
            Assert.AreEqual(0, comp.componentTargetedCount);
            Assert.AreEqual(1, comp.targetedWithoutTargetingCount);

            // Target the Component (Component-based listeners should receive)
            "Hello".EmitAt((InstanceId)comp);
            Assert.AreEqual(1, comp.gameObjectTargetedCount);
            Assert.AreEqual(1, comp.componentTargetedCount);
            Assert.AreEqual(2, comp.targetedWithoutTargetingCount);

            // Original Emit(string, InstanceId) form should behave the same as EmitAt
            "Hello".Emit((InstanceId)go);
            Assert.AreEqual(2, comp.gameObjectTargetedCount);
            Assert.AreEqual(1, comp.componentTargetedCount);
            Assert.AreEqual(3, comp.targetedWithoutTargetingCount);
        }

        [Test]
        public void EmitFromBroadcastSource()
        {
            GameObject go = new(
                nameof(EmitFromBroadcastSource),
                typeof(StringMessageAwareComponent)
            );
            _spawned.Add(go);
            StringMessageAwareComponent comp = go.GetComponent<StringMessageAwareComponent>();

            Assert.AreEqual(0, comp.gameObjectBroadcastCount);
            Assert.AreEqual(0, comp.componentBroadcastCount);
            Assert.AreEqual(0, comp.broadcastWithoutSourceCount);

            // Broadcast from GO (GO-based listeners should receive)
            "Hit".EmitFrom((InstanceId)go);
            Assert.AreEqual(1, comp.gameObjectBroadcastCount);
            Assert.AreEqual(0, comp.componentBroadcastCount);
            Assert.AreEqual(1, comp.broadcastWithoutSourceCount);

            // Broadcast from Component (Component-based listeners should receive)
            StringMessageAwareComponent compRef = comp; // explicit reference for readability
            "Hit".EmitFrom((InstanceId)compRef);
            Assert.AreEqual(1, comp.gameObjectBroadcastCount);
            Assert.AreEqual(1, comp.componentBroadcastCount);
            Assert.AreEqual(2, comp.broadcastWithoutSourceCount);
        }

        [Test]
        public void EmitUntargetedGlobalString()
        {
            GameObject go = new(
                nameof(EmitUntargetedGlobalString),
                typeof(StringMessageAwareComponent)
            );
            _spawned.Add(go);
            StringMessageAwareComponent comp = go.GetComponent<StringMessageAwareComponent>();

            Assert.AreEqual(0, comp.untargetedGlobalCount);

            // Untargeted shorthand
            "Saved".Emit();
            Assert.AreEqual(1, comp.untargetedGlobalCount);

            // Ensure a second emission increments again
            "Saved".Emit();
            Assert.AreEqual(2, comp.untargetedGlobalCount);
        }
    }
}

#endif
