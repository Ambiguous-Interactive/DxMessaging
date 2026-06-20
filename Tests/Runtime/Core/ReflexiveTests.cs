#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class ReflexiveTests : MessagingTestBase
    {
        [Test]
        public void ReflexiveSendModesRespectHierarchy()
        {
            GameObject grandParent = new(
                nameof(ReflexiveSendModesRespectHierarchy) + "_Grand",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(grandParent);
            GameObject parent = new(
                nameof(ReflexiveSendModesRespectHierarchy) + "_Parent",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(parent);
            GameObject child = new(
                nameof(ReflexiveSendModesRespectHierarchy) + "_Child",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(child);

            parent.transform.SetParent(grandParent.transform);
            child.transform.SetParent(parent.transform);

            SimpleMessageAwareComponent grandComponent =
                grandParent.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent parentComponent =
                parent.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent childComponent =
                child.GetComponent<SimpleMessageAwareComponent>();

            int grandCount = 0;
            int parentCount = 0;
            int childCount = 0;
            grandComponent.reflexiveTwoArgumentHandler = () => ++grandCount;
            parentComponent.reflexiveTwoArgumentHandler = () => ++parentCount;
            childComponent.reflexiveTwoArgumentHandler = () => ++childCount;

            // Flat should only target the specified GameObject
            ResetCounters();
            ReflexiveMessage flat = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                1,
                2
            );
            InstanceId parentId = parent;
            flat.EmitTargeted(parentId);
            Assert.AreEqual(0, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(0, childCount);

            // Downwards should reach parent and descendants
            ResetCounters();
            ReflexiveMessage downwards = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Downwards,
                1,
                2
            );
            downwards.EmitTargeted(parentId);
            Assert.AreEqual(0, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(1, childCount);

            // Upwards should reach target and all ancestors
            ResetCounters();
            ReflexiveMessage upwards = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Upwards,
                1,
                2
            );
            InstanceId childId = child;
            upwards.EmitTargeted(childId);
            Assert.AreEqual(1, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(1, childCount);

            // Combination of Upwards & Downwards should reach entire hierarchy once
            ResetCounters();
            ReflexiveMessage bothDirections = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Upwards | ReflexiveSendMode.Downwards,
                1,
                2
            );
            bothDirections.EmitTargeted(parentId);
            Assert.AreEqual(1, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(1, childCount);

            // OnlyIncludeActive should skip disabled receivers
            ResetCounters();
            childComponent.enabled = false;
            ReflexiveMessage downwardsActiveOnly = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Downwards | ReflexiveSendMode.OnlyIncludeActive,
                1,
                2
            );
            downwardsActiveOnly.EmitTargeted(parentId);
            Assert.AreEqual(0, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(0, childCount);
            childComponent.enabled = true;

            return;

            void ResetCounters()
            {
                grandCount = 0;
                parentCount = 0;
                childCount = 0;
            }
        }

        [Test]
        public void ReflexiveHandlesMultipleParameters()
        {
            GameObject host = new(
                nameof(ReflexiveHandlesMultipleParameters),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent component =
                host.GetComponent<SimpleMessageAwareComponent>();

            int twoArgCount = 0;
            int threeArgCount = 0;
            component.reflexiveTwoArgumentHandler = () => ++twoArgCount;
            component.reflexiveThreeArgumentHandler = () => ++threeArgCount;

            ReflexiveMessage twoArguments = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                27,
                42
            );
            InstanceId hostId = host;
            twoArguments.EmitTargeted(hostId);
            Assert.AreEqual(1, twoArgCount);
            Assert.AreEqual(0, threeArgCount);

            ReflexiveMessage threeArguments = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageThreeArguments),
                ReflexiveSendMode.Flat,
                1,
                2,
                3
            );
            threeArguments.EmitTargeted(hostId);
            Assert.AreEqual(1, twoArgCount);
            Assert.AreEqual(1, threeArgCount);
        }
    }
}

#endif
