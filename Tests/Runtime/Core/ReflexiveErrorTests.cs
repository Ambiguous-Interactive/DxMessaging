#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class ReflexiveErrorTests : MessagingTestBase
    {
        public enum DestroyedTargetKind
        {
            GameObject,
            Component,
        }

        private static IEnumerable<TestCaseData> MissingReceiverCases()
        {
            ReflexiveSendMode[] modes =
            {
                ReflexiveSendMode.Flat,
                ReflexiveSendMode.Upwards,
                ReflexiveSendMode.Downwards,
            };
            foreach (ReflexiveSendMode mode in modes)
            {
                yield return new TestCaseData(mode, 0).SetName($"{mode}_NoArguments");
                yield return new TestCaseData(mode, 1).SetName($"{mode}_OneArgument");
            }
        }

        private static IEnumerable<TestCaseData> SupportedReceiverCases()
        {
            ReflexiveSendMode[] modes =
            {
                ReflexiveSendMode.Flat,
                ReflexiveSendMode.Upwards,
                ReflexiveSendMode.Downwards,
            };
            foreach (ReflexiveSendMode mode in modes)
            {
                for (int argumentCount = 0; argumentCount <= 2; ++argumentCount)
                {
                    yield return new TestCaseData(mode, argumentCount).SetName(
                        $"{mode}_{argumentCount}Arguments_InactiveReceiver"
                    );
                }
            }
        }

        /// <remarks>
        /// Investigation (2026-08-03): Unity's overloaded null comparison is required before
        /// pattern matching a retained object reference. A destroyed reference still matches its
        /// managed GameObject or Component type, but dereferencing it throws. These cases prove
        /// hierarchy delivery skips both destroyed target shapes while ordinary bus handlers keep
        /// running.
        /// </remarks>
        [TestCase(DestroyedTargetKind.GameObject)]
        [TestCase(DestroyedTargetKind.Component)]
        public void DestroyedUnityTargetSkipsHierarchyAndContinuesBusDelivery(
            DestroyedTargetKind targetKind
        )
        {
            GameObject host = new(
                nameof(DestroyedUnityTargetSkipsHierarchyAndContinuesBusDelivery),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent component =
                host.GetComponent<SimpleMessageAwareComponent>();
            InstanceId target =
                targetKind == DestroyedTargetKind.GameObject
                    ? (InstanceId)host
                    : (InstanceId)component;
            Object.DestroyImmediate(host);
            Assert.That(
                target.Object == null,
                Is.True,
                $"[{targetKind}] Test setup must retain a destroyed Unity object reference."
            );

            MessageBus messageBus = new();
            MessageHandler handler = new(new InstanceId(9173), messageBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);
            int busHandlerCalls = 0;
            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                1,
                2
            );

            using (
                LeakWatcher watcher = new(
                    messageBus,
                    label: $"Destroyed reflexive {targetKind} target"
                )
            )
            {
                try
                {
                    _ = token.RegisterTargeted<ReflexiveMessage>(
                        target,
                        (in ReflexiveMessage _) => ++busHandlerCalls
                    );
                    token.Enable();

                    Assert.DoesNotThrow(
                        () => DispatchReflexive(messageBus, target, message),
                        $"[{targetKind}] Hierarchy delivery must not dereference a destroyed Unity target."
                    );
                    Assert.That(
                        busHandlerCalls,
                        Is.EqualTo(1),
                        $"[{targetKind}] Normal targeted bus delivery must continue after hierarchy delivery is skipped."
                    );
                }
                finally
                {
                    token.Disable();
                }
            }
        }

        [Test]
        public void UnknownMethodDoesNotThrowOrInvoke()
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
        }

        [TestCaseSource(nameof(MissingReceiverCases))]
        public void MissingReceiverIsSilentForSupportedArgumentCounts(
            ReflexiveSendMode sendMode,
            int argumentCount
        )
        {
            GameObject host = new(nameof(MissingReceiverIsSilentForSupportedArgumentCounts));
            _spawned.Add(host);
            InstanceId hostId = host;
            object[] arguments =
                argumentCount == 0 ? global::System.Array.Empty<object>() : new object[] { 1 };
            ReflexiveMessage message = new("NoSuchMethodOnComponent", sendMode, arguments);

            Assert.DoesNotThrow(
                () => message.EmitTargeted(hostId),
                $"[{sendMode}, arguments={argumentCount}] A missing reflexive receiver must be a silent no-op."
            );
        }

        [TestCaseSource(nameof(SupportedReceiverCases))]
        public void SupportedArgumentCountsInvokeInactiveMatchingReceiver(
            ReflexiveSendMode sendMode,
            int argumentCount
        )
        {
            GameObject receiver = new(
                nameof(SupportedArgumentCountsInvokeInactiveMatchingReceiver) + "_Receiver",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(receiver);
            GameObject target;
            switch (sendMode)
            {
                case ReflexiveSendMode.Flat:
                    target = receiver;
                    break;
                case ReflexiveSendMode.Upwards:
                    target = new(
                        nameof(SupportedArgumentCountsInvokeInactiveMatchingReceiver) + "_Target"
                    );
                    _spawned.Add(target);
                    target.transform.SetParent(receiver.transform);
                    break;
                case ReflexiveSendMode.Downwards:
                    target = new(
                        nameof(SupportedArgumentCountsInvokeInactiveMatchingReceiver) + "_Target"
                    );
                    _spawned.Add(target);
                    receiver.transform.SetParent(target.transform);
                    break;
                default:
                    throw new AssertionException($"Unexpected send mode: {sendMode}.");
            }

            SimpleMessageAwareComponent component =
                receiver.GetComponent<SimpleMessageAwareComponent>();
            int callCount = 0;
            string methodName;
            object[] arguments;
            if (argumentCount == 0)
            {
                component.reflexiveNoArgumentHandler = () => ++callCount;
                methodName = nameof(SimpleMessageAwareComponent.HandleReflexiveMessageNoArguments);
                arguments = global::System.Array.Empty<object>();
            }
            else if (argumentCount == 1)
            {
                component.reflexiveOneArgumentHandler = () => ++callCount;
                methodName = nameof(SimpleMessageAwareComponent.HandleReflexiveMessageOneArgument);
                arguments = new object[] { 1 };
            }
            else
            {
                component.reflexiveTwoArgumentHandler = () => ++callCount;
                methodName = nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments);
                arguments = new object[] { 1, 2 };
            }

            InstanceId targetId = target;
            receiver.SetActive(false);
            ReflexiveMessage message = new(methodName, sendMode, arguments);
            message.EmitTargeted(targetId);

            Assert.That(
                callCount,
                Is.EqualTo(1),
                $"[{sendMode}, arguments={argumentCount}] An inactive matching reflexive receiver must run once when OnlyIncludeActive is absent."
            );
        }

        [TestCase(ReflexiveSendMode.Flat)]
        [TestCase(ReflexiveSendMode.Upwards)]
        [TestCase(ReflexiveSendMode.Downwards)]
        public void SingleArgumentNativeCompatibilityAcceptsIgnoredAndAssignableArguments(
            ReflexiveSendMode sendMode
        )
        {
            GameObject receiver = new(
                nameof(SingleArgumentNativeCompatibilityAcceptsIgnoredAndAssignableArguments)
                    + "_Receiver",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(receiver);
            GameObject target;
            switch (sendMode)
            {
                case ReflexiveSendMode.Flat:
                    target = receiver;
                    break;
                case ReflexiveSendMode.Upwards:
                    target = new(
                        nameof(
                            SingleArgumentNativeCompatibilityAcceptsIgnoredAndAssignableArguments
                        ) + "_Target"
                    );
                    _spawned.Add(target);
                    target.transform.SetParent(receiver.transform);
                    break;
                case ReflexiveSendMode.Downwards:
                    target = new(
                        nameof(
                            SingleArgumentNativeCompatibilityAcceptsIgnoredAndAssignableArguments
                        ) + "_Target"
                    );
                    _spawned.Add(target);
                    receiver.transform.SetParent(target.transform);
                    break;
                default:
                    throw new AssertionException($"Unexpected send mode: {sendMode}.");
            }

            SimpleMessageAwareComponent component =
                receiver.GetComponent<SimpleMessageAwareComponent>();
            int ignoredArgumentCount = 0;
            int assignableArgumentCount = 0;
            component.reflexiveIgnoredArgumentHandler = () => ++ignoredArgumentCount;
            component.reflexiveObjectArgumentHandler = () => ++assignableArgumentCount;
            InstanceId targetId = target;

            ReflexiveMessage ignoredArgument = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageIgnoringArgument),
                sendMode,
                1
            );
            ignoredArgument.EmitTargeted(targetId);
            ReflexiveMessage assignableArgument = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageObjectArgument),
                sendMode,
                "derived value"
            );
            assignableArgument.EmitTargeted(targetId);

            Assert.That(
                ignoredArgumentCount,
                Is.EqualTo(1),
                $"[{sendMode}] Native one-argument compatibility must allow a receiver to ignore the value."
            );
            Assert.That(
                assignableArgumentCount,
                Is.EqualTo(1),
                $"[{sendMode}] Native one-argument compatibility must accept an assignable derived value."
            );
        }

        [Test]
        public void KnownMethodWithWrongArityDoesNotThrowOrInvoke()
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
            // overload. Failed signature lookups are not retained, and dispatch
            // remains a silent no-op (no throw, no invocation). This is distinct
            // from the unknown-name path above.
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
        }

        [Test]
        public void KnownMethodWithWrongParameterTypesDoesNotThrowOrInvoke()
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
        }

        private static void DispatchReflexive(
            MessageBus messageBus,
            InstanceId target,
            ReflexiveMessage message
        )
        {
            messageBus.TargetedBroadcast(ref target, ref message);
        }
    }
}

#endif
