#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    /// <summary>
    /// Tripwire for the IL2CPP check-elision annotations on the dispatch hot
    /// loops. The attributes are codegen-only (inert under Mono), so losing
    /// them in a refactor produces zero functional signal while silently
    /// regressing the published Standalone IL2CPP numbers. This contract is an
    /// implementation pin: when the hot-loop shape changes, update the method
    /// list rather than contorting the new design around it.
    /// </summary>
    [TestFixture]
    public sealed class Il2CppDispatchOptionContractTests
    {
        private static readonly string[] FullyElidedMethods =
        {
            "DispatchFlatSnapshot",
            "DispatchContextFlatSnapshot",
            "HasAnyDispatchEntries",
            "BroadcastGlobalUntargeted",
            "BroadcastGlobalTargeted",
            "BroadcastGlobalSourcedBroadcast",
        };

        private static readonly string[] NullCheckElidedMethods =
        {
            "InvokeGlobalUntargetedEntry",
            "InvokeGlobalTargetedEntry",
            "InvokeGlobalBroadcastEntry",
        };

        [Test]
        public void VendoredIl2CppSetOptionAttributeHasTheShapeIl2CppMatches()
        {
            Type attributeType = typeof(MessageBus).Assembly.GetType(
                "Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute"
            );
            Assert.IsNotNull(
                attributeType,
                "The vendored Il2CppSetOptionAttribute must live at the exact full name "
                    + "il2cpp.exe matches: Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute."
            );

            ConstructorInfo[] constructors = attributeType.GetConstructors(
                BindingFlags.Public | BindingFlags.Instance
            );
            Assert.AreEqual(
                1,
                constructors.Length,
                "Il2CppSetOptionAttribute must keep Unity's single (Option, object) constructor."
            );
            ParameterInfo[] parameters = constructors[0].GetParameters();
            Assert.AreEqual(2, parameters.Length, "Constructor must take (Option, object).");
            Assert.AreEqual(
                "Unity.IL2CPP.CompilerServices.Option",
                parameters[0].ParameterType.FullName,
                "First constructor parameter must be the vendored Option enum."
            );
            Assert.AreEqual(
                typeof(object),
                parameters[1].ParameterType,
                "Second constructor parameter must be object."
            );
        }

        [Test]
        public void HotDispatchLoopsCarryNullAndBoundsCheckElision(
            [ValueSource(nameof(FullyElidedMethods))] string methodName
        )
        {
            AssertHasOptions(methodName, expectBoundsElision: true);
        }

        [Test]
        public void GlobalEntryInvokersCarryNullCheckElision(
            [ValueSource(nameof(NullCheckElidedMethods))] string methodName
        )
        {
            AssertHasOptions(methodName, expectBoundsElision: false);
        }

        private static void AssertHasOptions(string methodName, bool expectBoundsElision)
        {
            MethodInfo[] matches = typeof(MessageBus)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(candidate => candidate.Name == methodName)
                .ToArray();
            Assert.AreEqual(
                1,
                matches.Length,
                $"Expected exactly one private MessageBus method named {methodName}; the "
                    + "IL2CPP check-elision contract list needs an update if the hot loop "
                    + "moved or gained an overload."
            );
            MethodInfo method = matches[0];

            Dictionary<int, bool> optionValues = new();
            foreach (CustomAttributeData attribute in method.GetCustomAttributesData())
            {
                if (
                    attribute.AttributeType.FullName
                    != "Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute"
                )
                {
                    continue;
                }

                int option = Convert.ToInt32(attribute.ConstructorArguments[0].Value);
                bool value = (bool)attribute.ConstructorArguments[1].Value;
                optionValues[option] = value;
            }

            // Option.NullChecks == 1, Option.ArrayBoundsChecks == 2 (Unity's enum values).
            Assert.IsTrue(
                optionValues.TryGetValue(1, out bool nullChecks) && !nullChecks,
                $"{methodName} must carry [Il2CppSetOption(Option.NullChecks, false)]."
            );
            if (expectBoundsElision)
            {
                Assert.IsTrue(
                    optionValues.TryGetValue(2, out bool boundsChecks) && !boundsChecks,
                    $"{methodName} must carry [Il2CppSetOption(Option.ArrayBoundsChecks, false)]."
                );
            }
        }
    }
}
#endif
