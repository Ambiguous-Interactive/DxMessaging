namespace DxMessaging.Tests.Runtime.AttributesOnly
{
    using System.Reflection;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    /// <summary>Runs generated shared rules in an assembly that references only the attributes.</summary>
    [TestFixture]
    public sealed class StandaloneAttributeRulesTests
    {
        /// <summary>Checks the real generator supplies constructors and optional defaults without the runtime.</summary>
        [TestCase(0, false, "unset")]
        [TestCase(42, true, "manual")]
        [TestCase(-1, false, "unset")]
        public void GeneratedConstructorWorksWithoutUnityOrMessagingRuntime(
            int count,
            bool explicitLabel,
            string expectedLabel
        )
        {
            RuleState state = explicitLabel ? new RuleState(count, "manual") : new RuleState(count);
            Assert.That(
                state.Count,
                Is.EqualTo(count),
                "The generated constructor assigns the rule value."
            );
            Assert.That(
                state.Label,
                Is.EqualTo(expectedLabel),
                "Callers receive the stated optional default."
            );
        }

        /// <summary>Checks the actual native consumer assembly has no engine, runtime, or compiler dependency.</summary>
        [Test]
        public void ConsumerAssemblyHasNoEngineRuntimeOrCompilerReferences()
        {
            Assembly consumer = typeof(RuleState).Assembly;
            Assert.That(
                consumer.GetName().Name,
                Is.EqualTo("WallstopStudios.DxMessaging.Tests.Runtime.AttributesOnly")
            );
            foreach (AssemblyName dependency in consumer.GetReferencedAssemblies())
            {
                string name = dependency.Name;
                Assert.That(
                    name,
                    Does.Not.StartWith("UnityEngine"),
                    $"Rule consumer dependency: {name}"
                );
                Assert.That(
                    name,
                    Does.Not.StartWith("UnityEditor"),
                    $"Rule consumer dependency: {name}"
                );
                Assert.That(
                    name,
                    Is.Not.EqualTo("WallstopStudios.DxMessaging"),
                    $"Rule consumer dependency: {name}"
                );
                Assert.That(
                    name,
                    Does.Not.StartWith("Microsoft.CodeAnalysis"),
                    $"Rule consumer dependency: {name}"
                );
            }
        }
    }

    /// <summary>Represents plain shared rules whose generated constructor has an optional argument.</summary>
    [DxAutoConstructor]
    public readonly partial struct RuleState
    {
        public readonly int Count;

        [DxOptionalParameter("unset")]
        public readonly string Label;
    }
}
