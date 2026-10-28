namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    [TestFixture]
    public sealed class MethodSignatureKeyValueTests
    {
        private static IEnumerable<TestCaseData> EqualityCases()
        {
            string[] pairs =
            {
                "default,default,true",
                "default,empty,false",
                "empty,default,false",
                "default,null,false",
                "null,default,false",
                "null,null,true",
                "null,empty,true",
                "empty,null,true",
                "empty,empty,true",
                "single,single,true",
                "single,string,false",
                "single,multi,false",
                "multi,single,false",
                "single,other,false",
                "multi,multi,true",
            };
            foreach (string pair in pairs)
            {
                string[] parts = pair.Split(',');
                yield return new TestCaseData(parts[0], parts[1], bool.Parse(parts[2])).SetName(
                    $"SignatureEquality_{parts[0]}_{parts[1]}"
                );
            }
        }

        private static MethodSignatureKey Key(string shape)
        {
            switch (shape)
            {
                case "default":
                    return default;
                case "null":
                    return new MethodSignatureKey("Receive", null);
                case "empty":
                    return new MethodSignatureKey("Receive", Type.EmptyTypes);
                case "single":
                    return new MethodSignatureKey("Receive", new[] { typeof(int) });
                case "string":
                    return new MethodSignatureKey("Receive", new[] { typeof(string) });
                case "multi":
                    return new MethodSignatureKey("Receive", new[] { typeof(int), typeof(string) });
                case "other":
                    return new MethodSignatureKey("Other", new[] { typeof(int) });
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }

        [TestCaseSource(nameof(EqualityCases))]
        public void EqualityHandlesDefaultAndOrderedSignatures(
            string leftShape,
            string rightShape,
            bool expected
        )
        {
            MethodSignatureKey left = Key(leftShape);
            MethodSignatureKey right = Key(rightShape);
            Assert.That(left.Equals(right), Is.EqualTo(expected), "Typed equality.");
            Assert.That(left.Equals((object)right), Is.EqualTo(expected), "Object equality.");
            Assert.That(left == right, Is.EqualTo(expected), "Equality operator.");
            Assert.That(left != right, Is.EqualTo(!expected), "Inequality operator.");
            Assert.That(left.Equals(null), Is.False, "Null object is not a signature.");
            if (expected)
            {
                Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
            }
        }

        private static IEnumerable<TestCaseData> MutationCases()
        {
            foreach (int count in new[] { 1, 2, 4, 8 })
            {
                HashSet<int> positions = new() { 0, count / 2, count - 1 };
                foreach (int position in positions)
                {
                    foreach (bool insertedFirst in new[] { false, true })
                    {
                        yield return new TestCaseData(count, position, insertedFirst).SetName(
                            $"SignatureSnapshot_Count{count}_Position{position}_InsertedFirst{insertedFirst}"
                        );
                    }
                }
            }
        }

        [TestCaseSource(nameof(MutationCases))]
        public void CallerMutationPreservesEqualityAndDictionaryLookup(
            int count,
            int position,
            bool insertedFirst
        )
        {
            Type[] types = new Type[count];
            Array.Fill(types, typeof(int));
            MethodSignatureKey key = new("Receive", types);
            MethodSignatureKey original = new("Receive", (Type[])types.Clone());
            int originalHash = key.GetHashCode();
            Dictionary<MethodSignatureKey, int> dictionary = new();
            if (insertedFirst)
            {
                dictionary.Add(key, 17);
            }
            types[position] = typeof(string);
            if (!insertedFirst)
            {
                dictionary.Add(key, 17);
            }
            MethodSignatureKey mutated = new("Receive", types);
            bool equalsOriginal = key.Equals(original);
            bool foundOriginal = dictionary.TryGetValue(original, out int value);
            bool equalsMutated = key.Equals(mutated);
            Assert.That(
                equalsOriginal,
                Is.True,
                $"Captured equality={equalsOriginal}; independent lookup={foundOriginal}, value={value}."
            );
            Assert.That(
                foundOriginal,
                Is.True,
                "Lookup runs independently of equality assertions."
            );
            Assert.That(value, Is.EqualTo(17));
            Assert.That(equalsMutated, Is.False, "Later metadata is a distinct signature.");
            Assert.That(key.GetHashCode(), Is.EqualTo(originalHash));
        }

        [Test]
        public void DefaultKeyCanBeLookedUpInDictionary()
        {
            Dictionary<MethodSignatureKey, int> dictionary = new() { [default] = 23 };
            Assert.That(dictionary.TryGetValue(default, out int value), Is.True);
            Assert.That(value, Is.EqualTo(23));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MessageMetadataMutationPreservesCapturedSignature(bool explicitTypes)
        {
            object[] arguments = { 1, 2 };
            ReflexiveMessage message = explicitTypes
                ? new ReflexiveMessage(
                    "Receive",
                    ReflexiveSendMode.Flat,
                    arguments,
                    new[] { typeof(int), typeof(int) }
                )
                : new ReflexiveMessage("Receive", ReflexiveSendMode.Flat, arguments);
            MethodSignatureKey original = new("Receive", new[] { typeof(int), typeof(int) });
            message.parameterTypes[0] = typeof(object);
            Assert.That(message.signatureKey, Is.EqualTo(original));
        }

        [Test]
        public void NullMethodNameKeepsArgumentValidation()
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
                new MethodSignatureKey(null, Type.EmptyTypes)
            );
            Assert.That(exception.ParamName, Is.EqualTo("methodName"));
        }
    }
}
