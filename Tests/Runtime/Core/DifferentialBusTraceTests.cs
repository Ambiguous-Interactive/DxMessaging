#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    /// <summary>First oracle increment: actual token replay and a dispatch-suppressing mutant, plus pure trace mechanics.</summary>
    public sealed class DifferentialBusTraceTests
    {
        [Test]
        public void GeneratorVersionPinsKnownSeedPrefix()
        {
            BusTraceSequence sequence = DifferentialBusTrace.Generate(
                MessageScenario.Untargeted(),
                17,
                4
            );
            Assert.That(
                BusTraceSequence.GeneratorVersion,
                Is.EqualTo(1),
                "Changing generation requires a new version and a reviewed replay fixture."
            );
            CollectionAssert.AreEqual(
                new[]
                {
                    "Register(token=0,context=0,value=1409999377,priority=1)",
                    "Emit(token=0,context=0,value=-576100708,priority=-1)",
                    "Disable(token=1,context=0,value=1944224582,priority=1)",
                    "Disable(token=1,context=1,value=1180700304,priority=0)",
                },
                sequence.Operations.Select(operation => operation.ToString()),
                "Generator version 1, seed 17 must retain its exact replay prefix across runtime profiles."
            );
        }

        [Test]
        public void ShrinkerPropagatesInfrastructureFailuresInsteadOfClassifyingThem()
        {
            BusTraceSequence sequence = DifferentialBusTrace.Generate(
                MessageScenario.Untargeted(),
                17,
                2
            );
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () =>
                    DifferentialBusTrace.Shrink(
                        sequence,
                        _ => throw new InvalidOperationException("infrastructure failure")
                    ),
                "Infrastructure failures must fail closed rather than become shrinkable semantic mismatches."
            );
            Assert.That(
                error.Message,
                Is.EqualTo("infrastructure failure"),
                "Shrinking must preserve the infrastructure failure."
            );
            Assert.Throws<ArgumentException>(
                () => DifferentialBusTrace.Shrink(sequence, _ => null),
                "A passing trace cannot supply a failing shrink predicate."
            );
        }

        [Test]
        public void SeededGenerationIsRepeatableAndValid(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(0, 1, 42)] int seed
        )
        {
            BusTraceSequence first = DifferentialBusTrace.Generate(scenario, (uint)seed, 32);
            BusTraceSequence second = DifferentialBusTrace.Generate(scenario, (uint)seed, 32);
            Assert.That(
                DifferentialBusTrace.IsValid(first),
                Is.True,
                $"[{scenario.Kind}] seed={seed}: generated handle dependencies must be valid."
            );
            CollectionAssert.AreEqual(
                first.Operations.Select(operation => operation.ToString()),
                second.Operations.Select(operation => operation.ToString()),
                $"[{scenario.Kind}] seed={seed}: identical seeds must produce identical operations."
            );
            Assert.That(
                first.Operations.Count,
                Is.EqualTo(32),
                $"[{scenario.Kind}] seed={seed}: generation must honor sequence length."
            );
        }

        [Test]
        public void ProductionReplaysMatchAcrossFreshBuses(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(0, 17)] int seed
        )
        {
            BusTraceSequence sequence = DifferentialBusTrace.Generate(scenario, (uint)seed, 32);
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            Assert.That(
                control.All(observation => observation.Exception == null),
                Is.True,
                $"[{scenario.Kind}] seed={seed}: valid baseline operations must not agree only because both adapters fail."
            );
            BusTraceMismatch mismatch = Evaluate(sequence, dropEmits: false);
            Assert.That(
                mismatch,
                Is.Null,
                mismatch?.BuildReport(sequence)
                    ?? $"[{scenario.Kind}] seed={seed}: identical production implementations must match."
            );
        }

        [Test]
        public void ConcurrentAdaptersDoNotShareRegistrationsOrCallbacks(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using MessageBusTraceAdapter left = CreateAdapter(scenario, false);
            using MessageBusTraceAdapter right = CreateAdapter(scenario, false);
            left.Execute(new BusTraceOperation(BusTraceOperationKind.Register));
            BusTraceOperation emit = new(BusTraceOperationKind.Emit, value: 13);
            Assert.That(
                right.Execute(emit).Callbacks,
                Is.Empty,
                $"[{scenario.Kind}]: a fresh isolated bus must not receive another bus's registration."
            );
            Assert.That(
                left.Execute(emit).Callbacks.Count,
                Is.EqualTo(1),
                $"[{scenario.Kind}]: the owning bus must still receive its callback."
            );
            Assert.That(
                right.Execute(emit).Callbacks,
                Is.Empty,
                $"[{scenario.Kind}]: replay output must not retain another adapter's callbacks."
            );
        }

        [Test]
        public void FaultyDispatchAdapterIsDetectedAndShrunkToRegisterThenEmit(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence original = DifferentialBusTrace.Generate(scenario, 17, 24);
            BusTraceMismatch mismatch = Evaluate(original, dropEmits: true);
            Assert.That(
                mismatch,
                Is.Not.Null,
                $"[{scenario.Kind}]: suppressing real dispatch must produce a mismatch."
            );
            Assert.That(mismatch.Category, Is.EqualTo("callbacks"), mismatch.BuildReport(original));
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                original,
                sequence => Evaluate(sequence, dropEmits: true)
            );
            Assert.That(
                DifferentialBusTrace.IsValid(minimal),
                Is.True,
                $"[{scenario.Kind}]: shrinking must preserve handle dependencies."
            );
            CollectionAssert.AreEqual(
                new[] { BusTraceOperationKind.Register, BusTraceOperationKind.Emit },
                minimal.Operations.Select(operation => operation.Kind),
                $"[{scenario.Kind}]: a dropped callback needs only its registration and emit."
            );
            BusTraceMismatch replayed = Evaluate(minimal, dropEmits: true);
            Assert.That(
                replayed,
                Is.Not.Null,
                $"[{scenario.Kind}]: the minimized real-bus failure must replay."
            );
            Assert.That(replayed.Index, Is.EqualTo(1), replayed.BuildReport(minimal));
            Assert.That(
                Evaluate(minimal, dropEmits: false),
                Is.Null,
                $"[{scenario.Kind}]: removing the faulty adapter must restore equivalence."
            );
        }

        [Test]
        public void FirstMismatchReportContainsReplayIdentityAndBothObservations(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                42,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 13),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 14),
                }
            );
            BusTraceObservation[] control =
            {
                Observation(),
                Observation("token=0,value=13"),
                Observation("token=0,value=14"),
            };
            BusTraceObservation[] candidate =
            {
                Observation(),
                Observation(),
                Observation("later mismatch"),
            };
            BusTraceMismatch mismatch = DifferentialBusTrace.Compare(control, candidate);
            Assert.That(
                mismatch.Index,
                Is.EqualTo(1),
                $"[{scenario.Kind}]: report must point to the earliest mismatch."
            );
            string report = mismatch.BuildReport(sequence);
            StringAssert.Contains(
                "sequenceLength=3",
                report,
                $"[{scenario.Kind}]: report must identify exact sequence length."
            );
            StringAssert.Contains(
                "[2] " + sequence.Operations[2],
                report,
                $"[{scenario.Kind}]: manual and minimized traces need all operations, not only their seed."
            );
            foreach (
                string expected in new[]
                {
                    "generator=1",
                    "seed=42",
                    "kind=" + scenario.Kind,
                    "firstMismatch=1",
                    "category=callbacks",
                    "operation=Emit",
                    "control:",
                    "candidate:",
                    "token=0,value=13",
                }
            )
            {
                StringAssert.Contains(
                    expected,
                    report,
                    $"[{scenario.Kind}]: missing diagnostic field {expected}."
                );
            }
        }

        [TestCase("order", "callbacks")]
        [TestCase("payload", "callbacks")]
        [TestCase("count", "callbacks")]
        [TestCase("exception", "exception")]
        [TestCase("state", "state")]
        [TestCase("equal", null)]
        public void ComparatorChecksEachObservable(string change, string category)
        {
            BusTraceObservation baseline = Observation("token=0,value=13", "token=1,value=13");
            BusTraceObservation changed = change switch
            {
                "order" => Observation("token=1,value=13", "token=0,value=13"),
                "payload" => Observation("token=0,value=99", "token=1,value=13"),
                "count" => Observation("token=0,value=13"),
                "exception" => new BusTraceObservation(
                    baseline.Callbacks,
                    baseline.State,
                    "failure"
                ),
                "state" => new BusTraceObservation(baseline.Callbacks, "different state", null),
                _ => Observation("token=0,value=13", "token=1,value=13"),
            };
            BusTraceMismatch mismatch = DifferentialBusTrace.Compare(
                new[] { baseline, baseline },
                new[] { baseline, changed }
            );
            Assert.That(
                mismatch?.Category,
                Is.EqualTo(category),
                $"change={change}, category={category ?? "none"}: comparator must inspect the requested observable."
            );
            if (category != null)
            {
                Assert.That(
                    mismatch.Index,
                    Is.EqualTo(1),
                    $"change={change}, category={category}: the first equal operation must not be reported."
                );
            }
        }

        [Test]
        public void ShrinkerPreservesHandleDependenciesAndOriginalFailureCategory()
        {
            BusTraceSequence original = new(
                MessageScenario.Untargeted(),
                9,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Enable, 1),
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(BusTraceOperationKind.Emit),
                    new BusTraceOperation(BusTraceOperationKind.Remove),
                }
            );
            int invalidEvaluations = 0;
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                original,
                sequence =>
                {
                    if (!DifferentialBusTrace.IsValid(sequence))
                    {
                        ++invalidEvaluations;
                    }
                    bool removal = sequence.Operations.Any(operation =>
                        operation.Kind == BusTraceOperationKind.Remove
                    );
                    bool emit = sequence.Operations.Any(operation =>
                        operation.Kind == BusTraceOperationKind.Emit
                    );
                    return removal ? new BusTraceMismatch(0, "state", Observation(), Observation())
                        : emit ? new BusTraceMismatch(0, "exception", Observation(), Observation())
                        : null;
                }
            );
            Assert.That(
                invalidEvaluations,
                Is.Zero,
                "The shrink predicate must never receive a sequence with invalid handle dependencies."
            );
            CollectionAssert.AreEqual(
                new[] { BusTraceOperationKind.Register, BusTraceOperationKind.Remove },
                minimal.Operations.Select(operation => operation.Kind),
                "Shrinking must keep the registration needed by removal, not switch to an unrelated exception failure."
            );
            Assert.That(
                minimal.Seed,
                Is.EqualTo(9),
                "A minimized sequence must preserve original seed provenance."
            );
        }

        [TestCase(-1)]
        [TestCase(257)]
        public void GeneratorRejectsOutOfRangeLengths(int length)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DifferentialBusTrace.Generate(MessageScenario.Untargeted(), 0, length),
                $"length={length}: unsupported lengths must be rejected."
            );
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(256)]
        public void GeneratorAcceptsEmptyAndBoundaryLengths(int length)
        {
            BusTraceSequence sequence = DifferentialBusTrace.Generate(
                MessageScenario.Targeted(),
                uint.MaxValue,
                length
            );
            Assert.That(
                sequence.Operations.Count,
                Is.EqualTo(length),
                $"length={length}: boundary generation must honor the requested length."
            );
            Assert.That(
                DifferentialBusTrace.IsValid(sequence),
                Is.True,
                $"length={length}: boundary generation must preserve handle validity."
            );
        }

        [Test]
        public void InvalidTraceIsRejectedBeforeAdapterConstruction()
        {
            BusTraceSequence invalid = new(
                MessageScenario.Untargeted(),
                1,
                new[] { new BusTraceOperation(BusTraceOperationKind.Remove) }
            );
            bool constructed = false;
            Assert.Throws<ArgumentException>(
                () =>
                    DifferentialBusTrace.Replay(
                        invalid,
                        _ =>
                        {
                            constructed = true;
                            return null;
                        }
                    ),
                "Removing a nonexistent handle must fail validation."
            );
            Assert.That(
                constructed,
                Is.False,
                "An invalid sequence must not create an implementation or mutate bus state."
            );
        }

        [Test]
        public void ReplayPreservesBothExecutionAndCleanupFailures()
        {
            BusTraceSequence sequence = new(
                MessageScenario.Untargeted(),
                1,
                new[] { new BusTraceOperation(BusTraceOperationKind.Emit) }
            );
            ThrowingAdapter adapter = new();
            AggregateException error = Assert.Throws<AggregateException>(
                () => DifferentialBusTrace.Replay(sequence, _ => adapter),
                "A cleanup failure must not hide an earlier execution failure."
            );
            CollectionAssert.AreEqual(
                new[] { "execute failure", "cleanup failure" },
                error.InnerExceptions.Select(exception => exception.Message),
                "Both original failures must remain available in their original order."
            );
            Assert.That(
                adapter.Disposed,
                Is.True,
                "Replay must attempt cleanup even when execution fails."
            );
        }

        private static BusTraceObservation Observation(params string[] callbacks) =>
            new(callbacks, "state", null);

        private static BusTraceMismatch Evaluate(BusTraceSequence sequence, bool dropEmits) =>
            DifferentialBusTrace.Compare(
                DifferentialBusTrace.Replay(sequence, scenario => CreateAdapter(scenario, false)),
                DifferentialBusTrace.Replay(
                    sequence,
                    scenario => CreateAdapter(scenario, dropEmits)
                )
            );

        private static MessageBusTraceAdapter CreateAdapter(
            MessageScenario scenario,
            bool dropEmits
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionEnabled: false
            );
            bus.DiagnosticsMode = false;
            return new MessageBusTraceAdapter(
                scenario,
                bus,
                dropEmits ? new DropEmitsBus(bus) : null
            );
        }

        /// <summary>Intentional mutant: suppresses actual bus emission rather than editing a recorded trace.</summary>
        private sealed class DropEmitsBus : DelegatingMessageBus
        {
            internal DropEmitsBus(IMessageBus inner)
                : base(inner) { }

            public override void UntargetedBroadcast<TMessage>(ref TMessage message) { }

            public override void TargetedBroadcast<TMessage>(
                ref InstanceId target,
                ref TMessage message
            ) { }

            public override void SourcedBroadcast<TMessage>(
                ref InstanceId source,
                ref TMessage message
            ) { }
        }

        /// <summary>Pure harness-error fixture; it does not simulate message routing.</summary>
        private sealed class ThrowingAdapter : IBusTraceAdapter
        {
            internal bool Disposed { get; private set; }

            public BusTraceObservation Execute(BusTraceOperation operation) =>
                throw new InvalidOperationException("execute failure");

            public void Dispose()
            {
                Disposed = true;
                throw new InvalidOperationException("cleanup failure");
            }
        }
    }
}
#endif
