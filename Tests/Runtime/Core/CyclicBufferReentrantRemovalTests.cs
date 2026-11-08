namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.DataStructure;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    /// <summary>Protects callback mutations from stale diagnostic-history publication.</summary>
    /// <remarks>
    /// 2026-11-04: A removal callback could overwrite its own additions or reuse an outer
    /// operation's scratch list. Removal now rejects recursive removal and aborts after
    /// another callback mutation, retaining the mutation and releasing scratch ownership.
    /// </remarks>
    public sealed class CyclicBufferReentrantRemovalTests
    {
        [Test]
        [Combinatorial]
        public void CallbackMutationsNeverPublishStaleScratch(
            [Values(
                "Add",
                "Clear",
                "Grow",
                "Shrink",
                "Zero",
                "Set",
                "Remove",
                "RemoveAll",
                "SameCapacity",
                "Read",
                "OtherBuffer"
            )]
                string mutation,
            [Values(false, true)] bool removeAll,
            [Values(false, true)] bool wrapped
        )
        {
            CyclicBuffer<int> buffer = CreateBuffer(wrapped);
            CyclicBuffer<int> other = new(3, new[] { 7, 8, 9 });
            int originalCapacity = buffer.Capacity;
            bool invoked = false;
            bool allowed =
                mutation == "SameCapacity" || mutation == "Read" || mutation == "OtherBuffer";
            Action mutate = () =>
            {
                switch (mutation)
                {
                    case "Add":
                        buffer.Add(4);
                        break;
                    case "Clear":
                        buffer.Clear();
                        break;
                    case "Grow":
                        buffer.Resize(7);
                        break;
                    case "Shrink":
                        buffer.Resize(2);
                        break;
                    case "Zero":
                        buffer.Resize(0);
                        break;
                    case "Set":
                        buffer[0] = 10;
                        break;
                    case "Remove":
                        buffer.Remove(3);
                        break;
                    case "RemoveAll":
                        buffer.RemoveAll(value => value == 3);
                        break;
                    case "SameCapacity":
                        buffer.Resize(originalCapacity);
                        break;
                    case "Read":
                        Assert.That(
                            buffer.ToArray(),
                            Is.EqualTo(new[] { 1, 2, 3 }),
                            "Callback reads must see the original ring."
                        );
                        Assert.That(
                            buffer.Contains(2),
                            Is.True,
                            "Contains must remain available during removal."
                        );
                        break;
                    case "OtherBuffer":
                        Assert.That(
                            other.Remove(8),
                            Is.True,
                            "Another buffer must own independent scratch."
                        );
                        other.Add(10);
                        break;
                }
            };
            Func<int, bool> match = value =>
            {
                if (value == 2 && !invoked)
                {
                    invoked = true;
                    mutate();
                }
                return value == 2;
            };
            Action remove = () =>
            {
                if (removeAll)
                {
                    Assert.That(
                        buffer.RemoveAll(value => match(value)),
                        Is.EqualTo(1),
                        "An unmodified removal must report one match."
                    );
                }
                else
                {
                    Assert.That(
                        buffer.Remove(2, new CallbackComparer(match)),
                        Is.True,
                        "An unmodified comparison removal must find its match."
                    );
                }
            };
            if (allowed)
            {
                remove();
            }
            else
            {
                Assert.Throws<InvalidOperationException>(
                    () => remove(),
                    $"{mutation}: callback mutation must abort stale publication."
                );
            }

            int[] expected = allowed
                ? new[] { 1, 3 }
                : mutation switch
                {
                    "Add" => wrapped ? new[] { 2, 3, 4 } : new[] { 1, 2, 3, 4 },
                    "Clear" => Array.Empty<int>(),
                    "Zero" => Array.Empty<int>(),
                    "Shrink" => new[] { 2, 3 },
                    "Set" => new[] { 10, 2, 3 },
                    _ => new[] { 1, 2, 3 },
                };
            Assert.That(invoked, Is.True, "The callback mutation must actually execute.");
            Assert.That(
                buffer.ToArray(),
                Is.EqualTo(expected),
                $"{mutation}: only completed callback writes may survive an aborted removal."
            );
            Assert.That(
                buffer.Count,
                Is.EqualTo(expected.Length),
                $"{mutation}: Count must agree with retained entries."
            );
            int expectedCapacity = mutation switch
            {
                "Grow" => 7,
                "Shrink" => 2,
                "Zero" => 0,
                _ => originalCapacity,
            };
            Assert.That(
                buffer.Capacity,
                Is.EqualTo(expectedCapacity),
                $"{mutation}: the callback's capacity change must survive."
            );
            Assert.That(
                ((List<int>)MessageBusInspection.ReadField(buffer, "_cache")).Count,
                Is.Zero,
                "Every removal exit must scrub scratch references."
            );
            buffer.Resize(5);
            buffer.Clear();
            buffer.Add(42);
            Assert.That(
                buffer.Remove(42),
                Is.True,
                "Removal ownership must be released for later reuse."
            );
            Assert.That(buffer.Count, Is.Zero, "Later reuse must leave an empty buffer.");
            Assert.That(
                ((List<int>)MessageBusInspection.ReadField(buffer, "_cache")).Count,
                Is.Zero,
                "Later reuse must also scrub scratch."
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CaughtRecursiveRemovalLeavesOuterScratchOwned(bool removeAll)
        {
            CyclicBuffer<int> buffer = CreateBuffer(true);
            int calls = 0;
            Func<int, bool> match = value =>
            {
                ++calls;
                if (value == 2)
                {
                    Assert.Throws<InvalidOperationException>(
                        () => buffer.Remove(99),
                        "Even a no-match recursive removal must not acquire outer scratch."
                    );
                    Assert.Throws<InvalidOperationException>(
                        () => buffer.RemoveAll(_ => false),
                        "Even a no-op recursive predicate must not acquire outer scratch."
                    );
                }
                return value == 2;
            };
            if (removeAll)
            {
                Assert.That(
                    buffer.RemoveAll(value => match(value)),
                    Is.EqualTo(1),
                    "Caught recursive rejection must leave one outer match."
                );
            }
            else
            {
                Assert.That(
                    buffer.Remove(2, new CallbackComparer(match)),
                    Is.True,
                    "Caught recursive rejection must retain the outer comparison."
                );
            }
            Assert.That(
                calls,
                Is.EqualTo(removeAll ? 3 : 2),
                "Rejected recursive callbacks must not alter outer traversal."
            );
            Assert.That(
                buffer.ToArray(),
                Is.EqualTo(new[] { 1, 3 }),
                "Only the outer matched entry may be removed."
            );
            Assert.That(
                ((List<int>)MessageBusInspection.ReadField(buffer, "_cache")).Count,
                Is.Zero,
                "Caught recursive rejection must not leak scratch references."
            );
        }

        [Test]
        [Combinatorial]
        public void CallbackExceptionRetainsItsIdentityAndCompletedMutation(
            [Values(false, true)] bool removeAll,
            [Values(false, true)] bool mutate
        )
        {
            CyclicBuffer<int> buffer = CreateBuffer(false);
            InvalidOperationException sentinel = new("Original callback failure.");
            Func<int, bool> match = value =>
            {
                if (value == 2)
                {
                    if (mutate)
                    {
                        buffer.Add(4);
                    }
                    throw sentinel;
                }
                return true;
            };
            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    if (removeAll)
                    {
                        buffer.RemoveAll(value => match(value));
                    }
                    else
                    {
                        buffer.Remove(2, new CallbackComparer(value => value != 1 && match(value)));
                    }
                },
                "The callback's own exception must propagate."
            );
            Assert.That(
                actual,
                Is.SameAs(sentinel),
                "Mutation detection must not replace an exception thrown by the callback."
            );
            Assert.That(
                buffer.ToArray(),
                Is.EqualTo(mutate ? new[] { 1, 2, 3, 4 } : new[] { 1, 2, 3 }),
                "No partially compacted outer result may publish after a throw."
            );
            Assert.That(
                ((List<int>)MessageBusInspection.ReadField(buffer, "_cache")).Count,
                Is.Zero,
                "Throwing callbacks must scrub scratch."
            );
            Assert.That(
                buffer.Remove(2),
                Is.True,
                "Throwing callbacks must release removal ownership."
            );
        }

        [TestCase("Add")]
        [TestCase("Clear")]
        [TestCase("Filter")]
        [TestCase("Resize")]
        [TestCase("Read")]
        [TestCase("OtherLog")]
        public void PublicLogPredicatePreservesChangesOrRejectsNestedFiltering(string mutation)
        {
            RegistrationLog log = new(true);
            RegistrationLog other = new(true);
            for (int id = 1; id <= 3; ++id)
            {
                log.Log(Entry(id));
            }
            bool invoked = false;
            Func<int> clear = () =>
                log.Clear(entry =>
                {
                    if (entry.id.Id == 2 && !invoked)
                    {
                        invoked = true;
                        switch (mutation)
                        {
                            case "Add":
                                log.Log(Entry(4));
                                break;
                            case "Clear":
                                log.Clear();
                                break;
                            case "Filter":
                                log.Clear(candidate => candidate.id.Id == 3);
                                break;
                            case "Resize":
                                log.Resize(2);
                                break;
                            case "Read":
                                Assert.That(
                                    log.ToString(candidate => candidate.id.Id.ToString()),
                                    Is.EqualTo("[1, 2, 3]"),
                                    "Public callback reads must remain available."
                                );
                                break;
                            case "OtherLog":
                                other.Log(Entry(7));
                                other.Clear(_ => true);
                                break;
                        }
                    }
                    return entry.id.Id == 2;
                });
            bool allowed = mutation == "Read" || mutation == "OtherLog";
            if (allowed)
            {
                Assert.That(
                    clear(),
                    Is.EqualTo(1),
                    "Read-only and other-log callbacks must retain ordinary filtering."
                );
            }
            else
            {
                Assert.Throws<InvalidOperationException>(
                    () => clear(),
                    "Same-log history changes or nested filtering must abort the outer clear."
                );
            }
            int[] expected = mutation switch
            {
                "Add" => new[] { 1, 2, 3, 4 },
                "Clear" => Array.Empty<int>(),
                "Resize" => new[] { 2, 3 },
                "Filter" => new[] { 1, 2, 3 },
                _ => new[] { 1, 3 },
            };
            Assert.That(invoked, Is.True, "The public predicate transition must run.");
            Assert.That(
                log.Registrations.Select(entry => entry.id.Id),
                Is.EqualTo(expected),
                "The public log must retain callback writes without a stale outer rebuild."
            );
            log.Clear();
            log.Log(Entry(42));
            Assert.That(
                log.Clear(_ => true),
                Is.EqualTo(1),
                "Public filtering must remain reusable after either exit."
            );
            Assert.That(
                log.Registrations,
                Is.Empty,
                "Public reuse must leave no retained history."
            );
        }

        private static CyclicBuffer<int> CreateBuffer(bool wrapped)
        {
            CyclicBuffer<int> buffer = new(wrapped ? 3 : 5);
            if (wrapped)
            {
                buffer.Add(-1);
                buffer.Add(0);
            }
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            return buffer;
        }

        private static MessagingRegistration Entry(int id)
        {
            return new MessagingRegistration(
                new InstanceId(id),
                typeof(int),
                RegistrationType.Register,
                RegistrationMethod.Untargeted
            );
        }

        private sealed class CallbackComparer : IEqualityComparer<int>
        {
            private readonly Func<int, bool> _match;

            internal CallbackComparer(Func<int, bool> match)
            {
                _match = match;
            }

            public bool Equals(int value, int other)
            {
                return _match(value);
            }

            public int GetHashCode(int value)
            {
                return value;
            }
        }
    }
}
