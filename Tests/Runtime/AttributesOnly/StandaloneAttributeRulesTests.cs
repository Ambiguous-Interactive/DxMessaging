namespace DxMessaging.Tests.Runtime.AttributesOnly
{
    using System;
    using System.Collections.Generic;
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
        public void GeneratedConstructorWorksWithoutUnityOrMessagingRuntime(int count, bool explicitLabel, string expectedLabel)
        {
            RuleState state = explicitLabel ? new RuleState(count, "manual") : new RuleState(count);
            Assert.That(state.Count, Is.EqualTo(count), "The generated constructor assigns the rule value.");
            Assert.That(state.Label, Is.EqualTo(expectedLabel), "Callers receive the stated optional default.");
        }

        /// <summary>Checks packaged constructors retain nested access and optional rule defaults.</summary>
        [TestCaseSource(nameof(NestedAccessibilityCases))]
        [Category("Fast")]
        public void PackagedConstructorsPreserveNestedRuleAccess(string access, int expectedVisibility, bool referenceType)
        {
            string context = $"access={access}; expectedVisibility={expectedVisibility}; referenceType={referenceType}";
            NestedRuleObservation observation = NestedRuleContainers.Read(access, referenceType);
            Assert.That(observation.Count, Is.EqualTo(42), "Assigned rule value: " + context);
            Assert.That(observation.Label, Is.EqualTo("nested"), "Optional label default: " + context);
            Assert.That(observation.ContainerVisibility, Is.EqualTo(expectedVisibility), "Container visibility: " + context);
            Assert.That(observation.TargetVisibility, Is.EqualTo(expectedVisibility), "Target visibility: " + context);
        }

        private static IEnumerable<TestCaseData> NestedAccessibilityCases()
        {
            // ECMA-335 II.23.1.15 defines the nested visibility values.
            foreach (
                (string access, int visibility) in new[] { ("public", 2), ("private", 3), ("protected", 4), ("internal", 5), ("protected internal", 7), ("private protected", 6) }
            )
            {
                foreach (bool referenceType in new[] { false, true })
                {
                    yield return new TestCaseData(access, visibility, referenceType).SetName($"PackagedConstructorsPreserveNestedRuleAccess({access},{referenceType})");
                }
            }
        }

        /// <summary>Checks the actual native consumer assembly has no engine, runtime, or compiler dependency.</summary>
        [Test]
        public void ConsumerAssemblyHasNoEngineRuntimeOrCompilerReferences()
        {
            Assembly consumer = typeof(RuleState).Assembly;
            Assert.That(consumer.GetName().Name, Is.EqualTo("WallstopStudios.DxMessaging.Tests.Runtime.AttributesOnly"));
            foreach (AssemblyName dependency in consumer.GetReferencedAssemblies())
            {
                string name = dependency.Name;
                Assert.That(name, Does.Not.StartWith("UnityEngine"), $"Rule consumer dependency: {name}");
                Assert.That(name, Does.Not.StartWith("UnityEditor"), $"Rule consumer dependency: {name}");
                Assert.That(name, Is.Not.EqualTo("WallstopStudios.DxMessaging"), $"Rule consumer dependency: {name}");
                Assert.That(name, Does.Not.StartWith("Microsoft.CodeAnalysis"), $"Rule consumer dependency: {name}");
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

    internal readonly struct NestedRuleObservation
    {
        internal readonly int Count;
        internal readonly string Label;
        internal readonly int ContainerVisibility;
        internal readonly int TargetVisibility;

        internal NestedRuleObservation(int count, string label, Type container, Type target)
        {
            Count = count;
            Label = label;
            ContainerVisibility = (int)(container.Attributes & TypeAttributes.VisibilityMask);
            TargetVisibility = (int)(target.Attributes & TypeAttributes.VisibilityMask);
        }
    }

    internal partial class NestedRuleContainers
    {
        internal static NestedRuleObservation Read(string access, bool referenceType)
        {
            return access switch
            {
                "public" => PublicEnvelope.Read(referenceType),
                "private" => PrivateEnvelope.Read(referenceType),
                "protected" => ProtectedEnvelope.Read(referenceType),
                "internal" => InternalEnvelope.Read(referenceType),
                "protected internal" => ProtectedInternalEnvelope.Read(referenceType),
                "private protected" => PrivateProtectedEnvelope.Read(referenceType),
                _ => throw new ArgumentOutOfRangeException(nameof(access), access, "Unknown rule access"),
            };
        }

        public partial class PublicEnvelope
        {
            [DxAutoConstructor]
            public partial class ClassRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            [DxAutoConstructor]
            public readonly partial struct StructRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            internal static NestedRuleObservation Read(bool referenceType)
            {
                if (referenceType)
                {
                    ClassRule classRule = new ClassRule(42);
                    return new NestedRuleObservation(classRule.Count, classRule.Label, typeof(PublicEnvelope), typeof(ClassRule));
                }
                StructRule structRule = new StructRule(42);
                return new NestedRuleObservation(structRule.Count, structRule.Label, typeof(PublicEnvelope), typeof(StructRule));
            }
        }

        private partial class PrivateEnvelope
        {
            [DxAutoConstructor]
            private partial class ClassRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            [DxAutoConstructor]
            private readonly partial struct StructRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            internal static NestedRuleObservation Read(bool referenceType)
            {
                if (referenceType)
                {
                    ClassRule classRule = new ClassRule(42);
                    return new NestedRuleObservation(classRule.Count, classRule.Label, typeof(PrivateEnvelope), typeof(ClassRule));
                }
                StructRule structRule = new StructRule(42);
                return new NestedRuleObservation(structRule.Count, structRule.Label, typeof(PrivateEnvelope), typeof(StructRule));
            }
        }

        protected partial class ProtectedEnvelope
        {
            [DxAutoConstructor]
            protected partial class ClassRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            [DxAutoConstructor]
            protected readonly partial struct StructRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            internal static NestedRuleObservation Read(bool referenceType)
            {
                if (referenceType)
                {
                    ClassRule classRule = new ClassRule(42);
                    return new NestedRuleObservation(classRule.Count, classRule.Label, typeof(ProtectedEnvelope), typeof(ClassRule));
                }
                StructRule structRule = new StructRule(42);
                return new NestedRuleObservation(structRule.Count, structRule.Label, typeof(ProtectedEnvelope), typeof(StructRule));
            }
        }

        internal partial class InternalEnvelope
        {
            [DxAutoConstructor]
            internal partial class ClassRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            [DxAutoConstructor]
            internal readonly partial struct StructRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            internal static NestedRuleObservation Read(bool referenceType)
            {
                if (referenceType)
                {
                    ClassRule classRule = new ClassRule(42);
                    return new NestedRuleObservation(classRule.Count, classRule.Label, typeof(InternalEnvelope), typeof(ClassRule));
                }
                StructRule structRule = new StructRule(42);
                return new NestedRuleObservation(structRule.Count, structRule.Label, typeof(InternalEnvelope), typeof(StructRule));
            }
        }

        protected internal partial class ProtectedInternalEnvelope
        {
            [DxAutoConstructor]
            protected internal partial class ClassRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            [DxAutoConstructor]
            protected internal readonly partial struct StructRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            internal static NestedRuleObservation Read(bool referenceType)
            {
                if (referenceType)
                {
                    ClassRule classRule = new ClassRule(42);
                    return new NestedRuleObservation(classRule.Count, classRule.Label, typeof(ProtectedInternalEnvelope), typeof(ClassRule));
                }
                StructRule structRule = new StructRule(42);
                return new NestedRuleObservation(structRule.Count, structRule.Label, typeof(ProtectedInternalEnvelope), typeof(StructRule));
            }
        }

        private protected partial class PrivateProtectedEnvelope
        {
            [DxAutoConstructor]
            private protected partial class ClassRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            [DxAutoConstructor]
            private protected readonly partial struct StructRule
            {
                public readonly int Count;

                [DxOptionalParameter("nested")]
                public readonly string Label;
            }

            internal static NestedRuleObservation Read(bool referenceType)
            {
                if (referenceType)
                {
                    ClassRule classRule = new ClassRule(42);
                    return new NestedRuleObservation(classRule.Count, classRule.Label, typeof(PrivateProtectedEnvelope), typeof(ClassRule));
                }
                StructRule structRule = new StructRule(42);
                return new NestedRuleObservation(structRule.Count, structRule.Label, typeof(PrivateProtectedEnvelope), typeof(StructRule));
            }
        }
    }
}
