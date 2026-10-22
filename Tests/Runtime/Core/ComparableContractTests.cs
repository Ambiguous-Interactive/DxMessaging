namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using NUnit.Framework;

    [TestFixture]
    public sealed class ComparableContractTests
    {
        private static readonly int[] NumericValues = { int.MinValue, -1, 0, 1, int.MaxValue };
        private static readonly MessageRegistrationHandle[] Handles =
        {
            default,
            MessageRegistrationHandle.CreateMessageRegistrationHandle(),
            MessageRegistrationHandle.CreateMessageRegistrationHandle(),
        };

        private static IEnumerable<TestCaseData> NumericPairs()
        {
            foreach (int left in NumericValues)
            {
                foreach (int right in NumericValues)
                {
                    yield return new TestCaseData(left, right);
                }
            }
        }

        private static IEnumerable<TestCaseData> HandlePairs()
        {
            for (int left = 0; left < Handles.Length; ++left)
            {
                for (int right = 0; right < Handles.Length; ++right)
                {
                    yield return new TestCaseData(left, right);
                }
            }
        }

        private static IEnumerable<TestCaseData> NumericBadArguments()
        {
            foreach (int value in NumericValues)
            {
                for (int kind = 0; kind < 3; ++kind)
                {
                    yield return new TestCaseData(value, kind);
                }
            }
        }

        private static IEnumerable<TestCaseData> HandleBadArguments()
        {
            for (int value = 0; value < Handles.Length; ++value)
            {
                for (int kind = 0; kind < 3; ++kind)
                {
                    yield return new TestCaseData(value, kind);
                }
            }
        }

        private static object Unrelated(int kind)
        {
            return kind switch
            {
                0 => new object(),
                1 => "unrelated",
                2 => 17,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }

        [TestCaseSource(nameof(NumericPairs))]
        public void IdentifierTypedAndBoxedOrderingAgreeWithNumericOrder(int left, int right)
        {
            InstanceId first = new(left);
            InstanceId second = new(right);
            int expected = Math.Sign(left.CompareTo(right));
            Assert.That(Math.Sign(first.CompareTo(second)), Is.EqualTo(expected));
            Assert.That(Math.Sign(((IComparable)first).CompareTo(second)), Is.EqualTo(expected));
        }

        [TestCaseSource(nameof(NumericValues))]
        public void IdentifierFollowsNull(int value)
        {
            Assert.That(((IComparable)new InstanceId(value)).CompareTo(null), Is.Positive);
        }

        [TestCaseSource(nameof(NumericBadArguments))]
        public void IdentifierRejectsUnrelatedArguments(int value, int kind)
        {
            IComparable comparable = new InstanceId(value);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                comparable.CompareTo(Unrelated(kind))
            );
            Assert.That(exception.ParamName, Is.EqualTo("rhs"));
        }

        [TestCaseSource(nameof(HandlePairs))]
        public void HandleBoxedOrderingAgreesWithTypedOrder(int left, int right)
        {
            int expected = Math.Sign(Handles[left].CompareTo(Handles[right]));
            Assert.That(
                Math.Sign(((IComparable)Handles[left]).CompareTo(Handles[right])),
                Is.EqualTo(expected)
            );
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void HandleFollowsNull(int index)
        {
            Assert.That(((IComparable)Handles[index]).CompareTo(null), Is.Positive);
        }

        [TestCaseSource(nameof(HandleBadArguments))]
        public void HandleRejectsUnrelatedArguments(int index, int kind)
        {
            IComparable comparable = Handles[index];
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                comparable.CompareTo(Unrelated(kind))
            );
            Assert.That(exception.ParamName, Is.EqualTo("obj"));
        }

        [TestCaseSource(nameof(NumericPairs))]
        public void HandlerTypedAndBoxedOrderingAgreeWithOwnerOrder(int left, int right)
        {
            MessageHandler first = new(new InstanceId(left));
            MessageHandler second = new(new InstanceId(right));
            int expected = Math.Sign(left.CompareTo(right));
            Assert.That(Math.Sign(first.CompareTo(second)), Is.EqualTo(expected));
            Assert.That(Math.Sign(((IComparable)first).CompareTo(second)), Is.EqualTo(expected));
        }

        [TestCaseSource(nameof(NumericValues))]
        public void HandlerTypedComparisonFollowsNull(int value)
        {
            MessageHandler handler = new(new InstanceId(value));
            Assert.That(((IComparable<MessageHandler>)handler).CompareTo(null), Is.Positive);
        }

        [TestCaseSource(nameof(NumericValues))]
        public void HandlerObjectComparisonFollowsNull(int value)
        {
            MessageHandler handler = new(new InstanceId(value));
            Assert.That(((IComparable)handler).CompareTo(null), Is.Positive);
        }

        [TestCaseSource(nameof(NumericBadArguments))]
        public void HandlerRejectsUnrelatedArguments(int value, int kind)
        {
            IComparable comparable = new MessageHandler(new InstanceId(value));
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                comparable.CompareTo(Unrelated(kind))
            );
            Assert.That(exception.ParamName, Is.EqualTo("obj"));
        }
    }
}
