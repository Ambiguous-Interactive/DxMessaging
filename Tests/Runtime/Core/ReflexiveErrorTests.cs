#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class ReflexiveErrorTests : MessagingTestBase
    {
        [UnityTest]
        public IEnumerator UnknownMethodDoesNotThrowOrInvoke()
        {
            GameObject host = new(
                nameof(UnknownMethodDoesNotThrowOrInvoke),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent comp = host.GetComponent<SimpleMessageAwareComponent>();

            int twoArgCount = 0;
            int threeArgCount = 0;
            comp.reflexiveTwoArgumentHandler = () => ++twoArgCount;
            comp.reflexiveThreeArgumentHandler = () => ++threeArgCount;

            // Use a method name that does not exist
            ReflexiveMessage bad = new("NoSuchMethodOnComponent", ReflexiveSendMode.Flat, 1, 2, 3);
            InstanceId hostId = host;
            bad.EmitTargeted(hostId);

            // Ensure nothing was called
            Assert.AreEqual(0, twoArgCount);
            Assert.AreEqual(0, threeArgCount);
            yield break;
        }

        [UnityTest]
        public IEnumerator KnownMethodWithWrongArityDoesNotThrowOrInvoke()
        {
            GameObject host = new(
                nameof(KnownMethodWithWrongArityDoesNotThrowOrInvoke),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent comp = host.GetComponent<SimpleMessageAwareComponent>();

            int twoArgCount = 0;
            int threeArgCount = 0;
            comp.reflexiveTwoArgumentHandler = () => ++twoArgCount;
            comp.reflexiveThreeArgumentHandler = () => ++threeArgCount;
            InstanceId hostId = host;

            // The method name exists, but the argument count does not match any
            // overload. Pinned behavior: the failed signature lookup is cached as
            // a null dispatcher, so dispatch is a silent no-op (no throw, no
            // invocation). This is distinct from the unknown-name path above.
            ReflexiveMessage wrongArity = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                1,
                2,
                3
            );
            Assert.DoesNotThrow(
                () => wrongArity.EmitTargeted(hostId),
                "A reflexive message naming a real method with the wrong arity must not throw."
            );
            Assert.AreEqual(
                0,
                twoArgCount,
                "The two-argument method must not be invoked with three arguments."
            );
            Assert.AreEqual(
                0,
                threeArgCount,
                "The three-argument method must not be invoked via the two-argument name."
            );

            // Control (anti-vacuity): the same method name with the correct arity
            // dispatches successfully.
            ReflexiveMessage correct = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                1,
                2
            );
            correct.EmitTargeted(hostId);
            Assert.AreEqual(
                1,
                twoArgCount,
                "Control failed: the correct-arity reflexive dispatch must invoke the method."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator KnownMethodWithWrongParameterTypesDoesNotThrowOrInvoke()
        {
            GameObject host = new(
                nameof(KnownMethodWithWrongParameterTypesDoesNotThrowOrInvoke),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent comp = host.GetComponent<SimpleMessageAwareComponent>();

            int twoArgCount = 0;
            comp.reflexiveTwoArgumentHandler = () => ++twoArgCount;
            InstanceId hostId = host;

            // The method name and arity exist, but the parameter types are wrong
            // (string, string instead of int, int). Pinned behavior: the typed
            // method lookup finds no match, so dispatch is a silent no-op.
            ReflexiveMessage wrongTypes = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                "first",
                "second"
            );
            Assert.DoesNotThrow(
                () => wrongTypes.EmitTargeted(hostId),
                "A reflexive message naming a real method with mismatched parameter "
                    + "types must not throw."
            );
            Assert.AreEqual(
                0,
                twoArgCount,
                "The method must not be invoked with mismatched parameter types."
            );

            // Control (anti-vacuity): the same method name with correctly typed
            // arguments dispatches successfully.
            ReflexiveMessage correct = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                1,
                2
            );
            correct.EmitTargeted(hostId);
            Assert.AreEqual(
                1,
                twoArgCount,
                "Control failed: the correctly-typed reflexive dispatch must invoke the method."
            );
            yield break;
        }
    }
}

#endif
