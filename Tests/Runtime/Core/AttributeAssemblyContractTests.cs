namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    /// <summary>Protects the engine-free attribute boundary and runtime type forwarding.</summary>
    [TestFixture]
    public sealed class AttributeAssemblyContractTests
    {
        private static IEnumerable<Type> PublicAttributes()
        {
            yield return typeof(DxAotMessageAttribute);
            yield return typeof(DxAutoConstructorAttribute);
            yield return typeof(DxBroadcastMessageAttribute);
            yield return typeof(DxIgnoreMissingBaseCallAttribute);
            yield return typeof(DxOptionalParameterAttribute);
            yield return typeof(DxTargetedMessageAttribute);
            yield return typeof(DxUntargetedMessageAttribute);
        }

        [TestCaseSource(nameof(PublicAttributes))]
        public void PublicAttributeBelongsToItsDedicatedAssembly(Type attribute)
        {
            Assert.That(
                attribute.Assembly.GetName().Name,
                Is.EqualTo("WallstopStudios.DxMessaging.Attributes"),
                $"{attribute.FullName} must be usable without the messaging runtime assembly."
            );
        }

        [TestCaseSource(nameof(PublicAttributes))]
        public void LegacyRuntimeTypeLookupResolvesTheSameAttribute(Type attribute)
        {
            Assembly runtime = typeof(MessageRegistrationToken).Assembly;
            Assert.That(
                runtime.GetType(attribute.FullName, throwOnError: true),
                Is.EqualTo(attribute),
                $"{attribute.FullName} must remain resolvable through the original runtime assembly."
            );
        }

        [Test]
        public void AttributeAssemblyDoesNotReferenceUnityOrMessagingRuntime()
        {
            Assembly attributes = typeof(DxAutoConstructorAttribute).Assembly;
            foreach (AssemblyName dependency in attributes.GetReferencedAssemblies())
            {
                string name = dependency.Name;
                Assert.That(
                    name,
                    Does.Not.StartWith("UnityEngine"),
                    $"Attribute dependency: {name}"
                );
                Assert.That(
                    name,
                    Does.Not.StartWith("UnityEditor"),
                    $"Attribute dependency: {name}"
                );
                Assert.That(
                    name,
                    Is.Not.EqualTo("WallstopStudios.DxMessaging"),
                    $"Attribute dependency: {name}"
                );
                Assert.That(
                    name,
                    Does.Not.StartWith("Microsoft.CodeAnalysis"),
                    $"Attribute dependency: {name}"
                );
            }
        }

        [Test]
        public void NativeOptimizationAttributeStaysInsideTheMessagingRuntime()
        {
            Assembly runtime = typeof(MessageRegistrationToken).Assembly;
            Type optimization = runtime.GetType(
                "Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute",
                throwOnError: true
            );
            Assert.That(
                optimization.Assembly,
                Is.EqualTo(runtime),
                "The internal IL2CPP marker is per runtime assembly."
            );
        }
    }
}
