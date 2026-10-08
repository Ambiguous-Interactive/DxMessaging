#if UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    [Category("NativeSdkCpu")]
    public sealed class ParallelPureHandlerTests
    {
        private readonly List<ParallelPureBufferOwner> _owned = new();
        private DiagnosticsScope _diagnostics;

        [SetUp]
        public void SetUp()
        {
            _owned.Clear();
            _diagnostics = new DiagnosticsScope(
                DiagnosticsTarget.Off,
                messageBufferSize: 16,
                diagnosticsStackTraces: false
            );
        }

        [TearDown]
        public void TearDown()
        {
            List<Exception> failures = new();
            try
            {
                foreach (ParallelPureBufferOwner owner in _owned)
                {
                    try
                    {
                        owner.Dispose();
                        owner.Dispose();
                        Assert.That(owner.HasPending, Is.False);
                        Assert.That(owner.Input.IsCreated, Is.False);
                        Assert.That(owner.Output.IsCreated, Is.False);
                        Assert.That(owner.Modes.IsCreated, Is.False);
                        Assert.That(owner.Threads.IsCreated, Is.False);
                        Assert.That(owner.ReleaseCount, Is.EqualTo(4));
                        if (owner.CompletedJob)
                        {
                            Assert.That(owner.CompletedNative, Is.True);
                        }
                    }
                    catch (Exception failure)
                    {
                        failures.Add(failure);
                    }
                }
                TestContext.WriteLine(
                    $"Parallel cleanup: owners={_owned.Count}; four root containers released once, every known job completed."
                );
                if (failures.Count != 0)
                {
                    throw new AggregateException(failures);
                }
            }
            finally
            {
                _diagnostics.Dispose();
                _owned.Clear();
            }
        }

        [Test]
        public void ParallelJobsPreserveCheckedRangesAndEveryNumericOutput(
            [Values] PureParallelPath path,
            [Values(1, 4, 16, 64, 128, 256)] int grain,
            [Values(0, 3)] int rounds,
            [Values(1, 17, 1027)] int count
        )
        {
            using ParallelPureBufferOwner owner = Create(count);
            NativeNumericPayload[] original = owner.Input.ToArray();
            long[] expected = Expected(owner, rounds);
            owner.Schedule(path, grain, rounds);
            owner.Complete();
            AssertResults(owner, expected, original);
            TestContext.WriteLine(
                $"Parallel row: path={path},grain={grain},rounds={rounds},items={count},checksum={Sum(owner.Results())},threads=[{ThreadIds(owner)}],mode=0; timing unmeasured."
            );
        }

        [Test]
        public void SeparateSubsystemOwnersScheduleAndMergeByLogicalId(
            [Values(1, 4, 16)] int shards,
            [Values(17, 1027)] int total,
            [Values(0, 3)] int rounds
        )
        {
            ParallelPureBufferOwner[] owners = CreateShards(total, shards);
            NativeNumericPayload[][] original = owners.Select(o => o.Input.ToArray()).ToArray();
            long[][] expectedByOwner = owners.Select(o => Expected(o, rounds)).ToArray();
            long[] expected = expectedByOwner.SelectMany(values => values).ToArray();
            foreach (ParallelPureBufferOwner owner in owners)
            {
                owner.ScheduleSubsystem(rounds);
            }
            for (int id = 0; id < owners.Length; ++id)
            {
                ParallelPureBufferOwner owner = owners[id];
                owner.Complete();
                AssertResults(owner, expectedByOwner[id], original[id]);
            }
            long[] actual = owners.SelectMany(owner => owner.Results()).ToArray();
            CollectionAssert.AreEqual(expected, actual);
            CollectionAssert.AreEqual(
                Enumerable.Range(0, total),
                owners
                    .SelectMany(owner => owner.Input.GetSubArray(1, owner.Count).ToArray())
                    .Select(p => p.Sequence)
            );
            TestContext.WriteLine(
                $"Subsystem row: shards={shards},rounds={rounds},items={total},counts=[{string.Join(",", owners.Select(o => o.Count))}],checksum={Sum(actual)},threads=[{string.Join(",", owners.SelectMany(o => o.Threads.GetSubArray(1, o.Count).ToArray()).Distinct().OrderBy(id => id))}],mode=0; separate owner buffers, logical-ID merge, timing unmeasured."
            );
        }

        [Test]
        public void LogicalReplayOrderIsExplicitAndIndependentOfJoinOrder()
        {
            ParallelPureBufferOwner[] owners = CreateShards(17, 4);
            foreach (ParallelPureBufferOwner owner in owners.Reverse())
            {
                owner.ScheduleSubsystem(0);
            }
            foreach (ParallelPureBufferOwner owner in owners.Reverse())
            {
                owner.Complete();
            }
            int[] ascending = owners
                .SelectMany(o => o.Input.GetSubArray(1, o.Count).ToArray())
                .Select(p => p.Sequence)
                .ToArray();
            int[] descending = owners
                .Reverse()
                .SelectMany(o => o.Input.GetSubArray(1, o.Count).ToArray())
                .Select(p => p.Sequence)
                .ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(0, 17), ascending);
            CollectionAssert.AreNotEqual(ascending, descending);
            TestContext.WriteLine(
                "Subsystem RED: fixed ascending-ID replay differs from reverse-ID replay; neither proves global completion FIFO."
            );
        }

        [Test]
        public void PendingWorkRejectsReadsResetAndReuseThenDisposeCompletes(
            [Values] PureParallelPath path
        )
        {
            ParallelPureBufferOwner owner = Create(1027);
            owner.Schedule(path, 64, 3);
            Assert.Throws<InvalidOperationException>(() => owner.Results());
            Assert.Throws<InvalidOperationException>(() => owner.Reset());
            Assert.Throws<InvalidOperationException>(() => owner.Schedule(path, 64, 3));
            owner.Dispose();
            Assert.That(owner.CompletedJob, Is.True);
            Assert.That(owner.CompletedNative, Is.True);
            Assert.That(owner.ReleaseCount, Is.EqualTo(4));
            Assert.Throws<ObjectDisposedException>(() => owner.Results());
            Assert.Throws<ObjectDisposedException>(() => owner.Reset());
            Assert.Throws<ObjectDisposedException>(() => owner.Schedule(path, 64, 3));
        }

        [Test]
        public void PartialManagedSubsystemSetupFailureUnwindsScheduledAndUnscheduledOwners()
        {
            ParallelPureBufferOwner[] owners = CreateShards(17, 4);
            InvalidOperationException expected = new("Expected managed subsystem setup failure.");
            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            {
                owners[0].ScheduleSubsystem(3);
                throw expected;
            });
            Assert.That(actual, Is.SameAs(expected));
            foreach (ParallelPureBufferOwner owner in owners)
            {
                owner.Dispose();
                Assert.That(owner.HasPending, Is.False);
                Assert.That(owner.ReleaseCount, Is.EqualTo(4));
            }
            Assert.That(owners[0].CompletedJob, Is.True);
            Assert.That(owners[0].CompletedNative, Is.True);
            foreach (ParallelPureBufferOwner owner in owners.Skip(1))
            {
                Assert.That(owner.CompletedJob, Is.False);
            }
        }

        [Test]
        public void InvalidArgumentsRejectBeforeSchedulingOrWriting()
        {
            using ParallelPureBufferOwner owner = Create(17);
            long[] original = owner.Output.ToArray();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Schedule(PureParallelPath.ParallelFor, 0, 0)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Schedule(PureParallelPath.ParallelFor, 257, 0)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Schedule(PureParallelPath.ParallelBatch, 1, -1)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Schedule(PureParallelPath.ParallelBatch, 1, 4)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owner.Schedule((PureParallelPath)99, 1, 0)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelPureBufferOwner(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelPureBufferOwner(4097));
            Assert.Throws<ArgumentOutOfRangeException>(() => CreateShards(1, 4));
            Assert.That(owner.HasPending, Is.False);
            CollectionAssert.AreEqual(original, owner.Output.ToArray());
        }

        [Test]
        public void CheckedBuffersResetAndReuseAcrossBothParallelPaths()
        {
            using ParallelPureBufferOwner owner = Create(17);
            NativeNumericPayload[] original = owner.Input.ToArray();
            foreach (PureParallelPath path in Enum.GetValues(typeof(PureParallelPath)))
            {
                for (int rounds = 0; rounds < 3; ++rounds)
                {
                    owner.Reset();
                    owner.Schedule(path, 4, rounds);
                    owner.Complete();
                    AssertResults(owner, Expected(owner, rounds), original);
                }
            }
        }

        [Test]
        public void CompletedParallelOutputsFeedActualPublicBusOnMainThread(
            [Values] PureParallelPath path,
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values("None", "Mutate", "Throw")] string boundary
        )
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            MessageBus bus = new() { DiagnosticsMode = false };
            bus.Trim(force: true);
            using LeakWatcher leaks = new(
                bus,
                label: $"Parallel pure replay {scenario.Kind}/{boundary}"
            );
            try
            {
                MessageHandler handler = new(new InstanceId(505_381), bus) { active = true };
                using MessageRegistrationToken token = MessageRegistrationToken.Create(
                    handler,
                    bus
                );
                token.DiagnosticMode = false;
                token.Enable();
                using ParallelPureBufferOwner owner = Create(3);
                NativeNumericPayload[] original = owner.Input.ToArray();
                long[] expectedValues = Expected(owner, 0);
                List<int> seen = new();
                int multiplier = 1;
                InvalidOperationException expected = new("Expected public consumer failure.");
                void OnPayload(in NativeNumericPayload payload)
                {
                    Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(mainThread));
                    Assert.That(payload.Value, Is.EqualTo(expectedValues[payload.Sequence]));
                    seen.Add(payload.Sequence * multiplier);
                    if (payload.Sequence == 1)
                    {
                        if (boundary == "Mutate")
                        {
                            multiplier = 10;
                        }
                        else if (boundary == "Throw")
                        {
                            throw expected;
                        }
                    }
                }
                InstanceId route = new(100);
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        _ = token.RegisterUntargeted<NativeNumericPayload>(OnPayload);
                        break;
                    case MessageKind.Targeted:
                        _ = token.RegisterTargeted<NativeNumericPayload>(route, OnPayload);
                        break;
                    case MessageKind.Broadcast:
                        _ = token.RegisterBroadcast<NativeNumericPayload>(route, OnPayload);
                        break;
                }
                void Dispatch(NativeNumericPayload payload)
                {
                    switch (scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            bus.UntargetedBroadcast(ref payload);
                            break;
                        case MessageKind.Targeted:
                            bus.TargetedBroadcast(ref route, ref payload);
                            break;
                        case MessageKind.Broadcast:
                            bus.SourcedBroadcast(ref route, ref payload);
                            break;
                    }
                }
                owner.Schedule(path, 1, 0);
                owner.Complete();
                AssertResults(owner, expectedValues, original);
                for (int index = 0; index < owner.Count; ++index)
                {
                    NativeNumericPayload payload = original[index + 1];
                    payload.Value = owner.Output[index + 1];
                    if (index == 1 && boundary == "Throw")
                    {
                        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
                            () =>
                                Dispatch(payload)
                        );
                        Assert.That(actual, Is.SameAs(expected));
                        CollectionAssert.AreEqual(new[] { 0, 1 }, seen);
                    }
                    else
                    {
                        Dispatch(payload);
                    }
                }
                CollectionAssert.AreEqual(
                    boundary == "Mutate" ? new[] { 0, 1, 20 } : new[] { 0, 1, 2 },
                    seen
                );
                AssertResults(owner, expectedValues, original);
            }
            finally
            {
                bus.Trim(force: true);
            }
        }

        private ParallelPureBufferOwner Create(int count, int offset = 0, int logicalProducer = -1)
        {
            ParallelPureBufferOwner owner = new(count, offset, logicalProducer);
            _owned.Add(owner);
            return owner;
        }

        private ParallelPureBufferOwner[] CreateShards(int total, int shards)
        {
            if (shards < 1 || 16 < shards || total < shards || 4096 < total)
            {
                throw new ArgumentOutOfRangeException(nameof(shards));
            }
            ParallelPureBufferOwner[] owners = new ParallelPureBufferOwner[shards];
            int offset = 0;
            for (int id = 0; id < shards; ++id)
            {
                int count = total / shards + (id < total % shards ? 1 : 0);
                owners[id] = Create(count, offset, id);
                offset += count;
            }
            return owners;
        }

        private static long[] Expected(ParallelPureBufferOwner owner, int rounds)
        {
            return owner
                .Input.GetSubArray(1, owner.Count)
                .ToArray()
                .Select(p => PureBatchKernels.Compute(p, rounds))
                .ToArray();
        }

        private static void AssertResults(
            ParallelPureBufferOwner owner,
            long[] expected,
            NativeNumericPayload[] original
        )
        {
            CollectionAssert.AreEqual(expected, owner.Results());
            CollectionAssert.AreEqual(original, owner.Input.ToArray());
            Assert.That(owner.Output[0], Is.EqualTo(ParallelPureBufferOwner.Guard));
            Assert.That(owner.Output[owner.Count + 1], Is.EqualTo(ParallelPureBufferOwner.Guard));
            Assert.That(owner.Input[0], Is.EqualTo(ParallelPureBufferOwner.InputGuard()));
            Assert.That(
                owner.Input[owner.Count + 1],
                Is.EqualTo(ParallelPureBufferOwner.InputGuard())
            );
            Assert.That(owner.Modes[0], Is.EqualTo(ParallelPureBufferOwner.MarkerGuard));
            Assert.That(
                owner.Modes[owner.Count + 1],
                Is.EqualTo(ParallelPureBufferOwner.MarkerGuard)
            );
            Assert.That(owner.Threads[0], Is.EqualTo(ParallelPureBufferOwner.MarkerGuard));
            Assert.That(
                owner.Threads[owner.Count + 1],
                Is.EqualTo(ParallelPureBufferOwner.MarkerGuard)
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(0, owner.Count).ToArray(),
                owner.Modes.GetSubArray(1, owner.Count).ToArray()
            );
            foreach (int id in owner.Threads.GetSubArray(1, owner.Count).ToArray())
            {
                Assert.That(id, Is.GreaterThanOrEqualTo(0));
            }
        }

        private static string ThreadIds(ParallelPureBufferOwner owner)
        {
            return string.Join(
                ",",
                owner.Threads.GetSubArray(1, owner.Count).ToArray().Distinct().OrderBy(id => id)
            );
        }

        private static long Sum(IEnumerable<long> values)
        {
            long sum = 0;
            foreach (long value in values)
            {
                sum = unchecked(sum + value);
            }
            return sum;
        }
    }
}
#endif
