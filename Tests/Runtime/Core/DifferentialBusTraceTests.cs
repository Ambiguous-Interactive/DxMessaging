#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Production replay, independent operation mutants, versioned generation, and dependency-preserving shrinking.</summary>
    public sealed class DifferentialBusTraceTests
    {
        private DiagnosticsScope _diagnostics;

        [SetUp]
        public void SetUp() =>
            _diagnostics = new DiagnosticsScope(
                DiagnosticsTarget.Off,
                messageBufferSize: 16,
                diagnosticsStackTraces: false
            );

        [TearDown]
        public void TearDown() => _diagnostics.Dispose();

        [Test]
        public void GeneratorVersionPinsKnownSeedPrefix([Values(1, 2, 3)] int version)
        {
            BusTraceSequence sequence = DifferentialBusTrace.Generate(
                MessageScenario.Untargeted(),
                17,
                4,
                generatorVersion: version
            );
            Assert.That(
                BusTraceSequence.GeneratorVersion,
                Is.EqualTo(3),
                "Changing generation requires a new version and a reviewed replay fixture."
            );
            CollectionAssert.AreEqual(
                new[]
                {
                    "Register(token=0,context=0,value=1409999377,priority=1)",
                    "Emit(token=0,context=0,value=-576100708,priority=-1)",
                    (
                        version == 1 ? "Disable"
                        : version == 2 ? "Emit"
                        : "Register"
                    ) + "(token=1,context=0,value=1944224582,priority=1)",
                    (
                        version == 1 ? "Disable"
                        : version == 2 ? "Register"
                        : "Emit"
                    ) + "(token=1,context=1,value=1180700304,priority=0)",
                },
                sequence.Operations.Select(operation => operation.ToString()),
                $"Generator version {version}, seed 17 must retain its exact replay prefix across runtime profiles."
            );
            Assert.That(
                sequence.Version,
                Is.EqualTo(version),
                $"version={version}: replay identity must preserve the requested generator."
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
            BusTraceSequence sequence = DifferentialBusTrace.Generate(
                scenario,
                (uint)seed,
                32,
                generatorVersion: 2
            );
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
        public void GeneratedVersionThreeReplaysMatchWithOnlyRequestedCallbackFailures(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(17, 42)] int seed
        )
        {
            BusTraceSequence sequence = DifferentialBusTrace.Generate(scenario, (uint)seed, 32);
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            for (int index = 0; index < control.Count; ++index)
            {
                string error = control[index].Exception;
                Assert.That(
                    error == null
                        || (
                            sequence.Operations[index].Kind == BusTraceOperationKind.EmitWithThrow
                            && error
                                == "System.InvalidOperationException: intentional trace callback failure"
                        ),
                    Is.True,
                    $"[{scenario.Kind}] seed={seed}, index={index}, operation={sequence.Operations[index]}: {control[index]}"
                );
            }
            BusTraceMismatch mismatch = Evaluate(sequence, dropEmits: false);
            Assert.That(
                mismatch,
                Is.Null,
                mismatch?.BuildReport(sequence)
                    ?? $"[{scenario.Kind}] seed={seed}: version 3 replays must match."
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
            BusTraceSequence original = DifferentialBusTrace.Generate(
                scenario,
                17,
                24,
                generatorVersion: 1
            );
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
                minimal.Version,
                Is.EqualTo(1),
                "Shrinking must retain version 1 provenance."
            );
            Assert.That(
                Evaluate(minimal, dropEmits: false),
                Is.Null,
                $"[{scenario.Kind}]: removing the faulty adapter must restore equivalence."
            );
        }

        [Test]
        public void DeferredResetMutantIsDetectedAndShrunk(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence original = ResetSequence(scenario);
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                original,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                original,
                kind => CreateAdapter(kind, false, deferReset: true)
            );
            BusTraceMismatch mismatch = DifferentialBusTrace.Compare(control, candidate);
            string report = DescribeReplay(original, control, candidate);
            Assert.That(control.All(observation => observation.Exception == null), Is.True, report);
            Assert.That(mismatch, Is.Not.Null, report);
            Assert.That(mismatch.Category, Is.EqualTo("callbacks"), report);
            Assert.That(mismatch.Index, Is.EqualTo(4), report);
            CollectionAssert.AreEqual(new[] { "token=0,value=13" }, control[4].Callbacks, report);
            CollectionAssert.AreEqual(
                new[] { "token=0,value=13", "token=1,value=13" },
                candidate[4].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                control.Select(observation => observation.State),
                candidate.Select(observation => observation.State),
                report
            );
            CollectionAssert.AreEqual(
                control.Select(observation => observation.Exception),
                candidate.Select(observation => observation.Exception),
                report
            );
            Assert.That(control[5].Callbacks, Is.Empty, report);
            CollectionAssert.AreEqual(new[] { "token=2,value=15" }, control[9].Callbacks, report);

            BusTraceSequence minimal = DifferentialBusTrace.Shrink(original, EvaluateDeferredReset);
            IReadOnlyList<BusTraceObservation> minimalControl = DifferentialBusTrace.Replay(
                minimal,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> minimalCandidate = DifferentialBusTrace.Replay(
                minimal,
                kind => CreateAdapter(kind, false, deferReset: true)
            );
            report += "\nMinimized:\n" + DescribeReplay(minimal, minimalControl, minimalCandidate);
            CollectionAssert.AreEqual(
                new[]
                {
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.EmitWithReset,
                },
                minimal.Operations.Select(operation => operation.Kind),
                report
            );
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            BusTraceMismatch replayed = DifferentialBusTrace.Compare(
                minimalControl,
                minimalCandidate
            );
            Assert.That(replayed?.Category, Is.EqualTo("callbacks"), report);
            Assert.That(replayed?.Index, Is.EqualTo(2), report);
            Assert.That(minimal.Version, Is.EqualTo(original.Version), report);
            Assert.That(minimal.Seed, Is.EqualTo(original.Seed), report);
            Assert.That(Evaluate(original, dropEmits: false), Is.Null, report);
            Assert.That(Evaluate(minimal, dropEmits: false), Is.Null, report);
        }

        [Test]
        public void CallbackTriggerWithoutMatchingRegistrationDoesNotRemainArmed(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool throwingCallback
        )
        {
            // Untargeted dispatch has no unmatched-context case; every kind has a disabled trigger.
            bool[] routes =
                scenario.Kind == MessageKind.Untargeted ? new[] { false } : new[] { false, true };
            foreach (bool otherRoute in routes)
            {
                using MessageBusTraceAdapter adapter = CreateAdapter(scenario, false);
                adapter.Execute(new BusTraceOperation(BusTraceOperationKind.Register));
                adapter.Execute(
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 1, priority: 1)
                );
                if (!otherRoute)
                {
                    adapter.Execute(new BusTraceOperation(BusTraceOperationKind.Disable));
                }
                BusTraceObservation untriggered = adapter.Execute(
                    new BusTraceOperation(
                        throwingCallback
                            ? BusTraceOperationKind.EmitWithThrow
                            : BusTraceOperationKind.EmitWithReset,
                        context: otherRoute ? 1 : 0,
                        value: 10
                    )
                );
                string label =
                    $"[{scenario.Kind}] throwingCallback={throwingCallback}, otherRoute={otherRoute}: {untriggered}";
                Assert.That(untriggered.Exception, Is.Null, label);
                CollectionAssert.AreEqual(
                    otherRoute ? Array.Empty<string>() : new[] { "token=1,value=10" },
                    untriggered.Callbacks,
                    label
                );
                adapter.Execute(new BusTraceOperation(BusTraceOperationKind.Enable));
                foreach (int value in new[] { 11, 12 })
                {
                    BusTraceObservation later = adapter.Execute(
                        new BusTraceOperation(BusTraceOperationKind.Emit, value: value)
                    );
                    Assert.That(later.Exception, Is.Null, label + "\n" + later);
                    CollectionAssert.AreEquivalent(
                        new[] { $"token=0,value={value}", $"token=1,value={value}" },
                        later.Callbacks,
                        label + "\n" + later
                    );
                }
            }
        }

        [Test]
        public void ThrowingResetActionIsObservedAndDoesNotPoisonLaterEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionEnabled: false
            );
            int resetCalls = 0;
            using MessageBusTraceAdapter adapter = new(
                scenario,
                bus,
                reset: () =>
                {
                    ++resetCalls;
                    throw new InvalidOperationException("reset action failure");
                }
            );
            adapter.Execute(new BusTraceOperation(BusTraceOperationKind.Register));
            BusTraceObservation failed = adapter.Execute(
                new BusTraceOperation(BusTraceOperationKind.EmitWithReset, value: 13)
            );
            Assert.That(
                failed.Exception,
                Is.EqualTo("System.InvalidOperationException: reset action failure"),
                $"[{scenario.Kind}]: {failed}"
            );
            BusTraceObservation later = adapter.Execute(
                new BusTraceOperation(BusTraceOperationKind.Emit, value: 14)
            );
            Assert.That(later.Exception, Is.Null, $"[{scenario.Kind}]: {later}");
            CollectionAssert.AreEqual(
                new[] { "token=0,value=14" },
                later.Callbacks,
                $"[{scenario.Kind}]: {later}"
            );
            Assert.That(
                resetCalls,
                Is.EqualTo(1),
                $"[{scenario.Kind}]: a throwing reset trigger must be cleared."
            );
        }

        [Test]
        public void CallbackResetLeavesOtherAdaptersAndStagedRegistrationsUsable(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using MessageBusTraceAdapter left = CreateAdapter(scenario, false);
            using MessageBusTraceAdapter right = CreateAdapter(scenario, false);
            BusTraceOperation register = new(BusTraceOperationKind.Register);
            left.Execute(register);
            right.Execute(register);
            BusTraceObservation reset = left.Execute(
                new BusTraceOperation(BusTraceOperationKind.EmitWithReset, value: 13)
            );
            Assert.That(reset.Exception, Is.Null, $"[{scenario.Kind}]: {reset}");
            BusTraceOperation emit = new(BusTraceOperationKind.Emit, value: 14);
            Assert.That(
                left.Execute(emit).Callbacks,
                Is.Empty,
                $"[{scenario.Kind}]: the reset bus must be silent."
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=14" },
                right.Execute(emit).Callbacks,
                $"[{scenario.Kind}]: reset must not affect the other bus."
            );
            left.Execute(new BusTraceOperation(BusTraceOperationKind.Disable));
            left.Execute(new BusTraceOperation(BusTraceOperationKind.Enable));
            BusTraceObservation replayed = left.Execute(emit);
            Assert.That(replayed.Exception, Is.Null, $"[{scenario.Kind}]: {replayed}");
            CollectionAssert.AreEqual(
                new[] { "token=0,value=14" },
                replayed.Callbacks,
                $"[{scenario.Kind}]: disable/enable must replay staging retained across reset."
            );
        }

        [Test]
        public void ResetTraceValidityRetainsHandleOwnershipAndVersion()
        {
            BusTraceOperation[] operations =
            {
                new(BusTraceOperationKind.Register),
                new(BusTraceOperationKind.EmitWithReset),
                new(BusTraceOperationKind.Remove),
            };
            MessageScenario scenario = MessageScenario.Untargeted();
            Assert.That(
                DifferentialBusTrace.IsValid(new BusTraceSequence(scenario, 17, operations)),
                Is.True,
                "Reset retains token-owned handles for stale removal."
            );
            Assert.That(
                DifferentialBusTrace.IsValid(
                    new BusTraceSequence(scenario, 17, operations, generatorVersion: 1)
                ),
                Is.False,
                "Version 1 cannot describe callback-time reset."
            );
            Assert.That(
                DifferentialBusTrace.IsValid(
                    new BusTraceSequence(scenario, 17, operations.Skip(1))
                ),
                Is.False,
                "A reset trigger requires its registration dependency."
            );
            Assert.That(
                DifferentialBusTrace
                    .Generate(scenario, 17, 256)
                    .Operations.Any(operation =>
                        operation.Kind == BusTraceOperationKind.EmitWithReset
                    ),
                Is.True,
                "Version 2 generation must exercise callback reset, not only hand-written fixtures."
            );
        }

        [TestCase(0)]
        [TestCase(4)]
        public void UnsupportedGeneratorVersionsAreRejected(int version)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DifferentialBusTrace.Generate(MessageScenario.Untargeted(), 17, 4, version),
                $"version={version}: unsupported provenance must be rejected."
            );
        }

        [Test]
        public void IndependentOperationMutantsAreDetectedAndShrunk(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values("order", "stale", "exception-cleanup", "diagnostics", "leak", "retention")]
                string fault
        )
        {
            List<BusTraceOperation> operations = new()
            {
                new(BusTraceOperationKind.Enable, token: 3),
                new(BusTraceOperationKind.Register),
            };
            switch (fault)
            {
                case "order":
                    operations.Add(new BusTraceOperation(BusTraceOperationKind.Register, token: 1));
                    break;
                case "stale":
                    operations.Add(new BusTraceOperation(BusTraceOperationKind.Remove));
                    operations.Add(new BusTraceOperation(BusTraceOperationKind.Register));
                    operations.Add(new BusTraceOperation(BusTraceOperationKind.RemoveStale));
                    break;
                case "exception-cleanup":
                    operations.Add(
                        new BusTraceOperation(BusTraceOperationKind.EmitWithThrow, value: 13)
                    );
                    break;
                case "diagnostics":
                    operations.Add(
                        new BusTraceOperation(BusTraceOperationKind.SetDiagnostics, value: 1)
                    );
                    break;
                case "retention":
                    operations.Add(
                        new BusTraceOperation(BusTraceOperationKind.SetDiagnostics, value: 1)
                    );
                    operations.Add(new BusTraceOperation(BusTraceOperationKind.Emit, value: 13));
                    operations.Add(new BusTraceOperation(BusTraceOperationKind.Remove));
                    break;
                case "leak":
                    operations.Add(new BusTraceOperation(BusTraceOperationKind.Remove));
                    break;
            }
            operations.Add(new BusTraceOperation(BusTraceOperationKind.Emit, value: 17));
            BusTraceSequence original = new(scenario, 509, operations);
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                original,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                original,
                kind => CreateMutant(kind, fault)
            );
            string report = $"fault={fault}\n" + DescribeReplay(original, control, candidate);
            BusTraceMismatch mismatch = DifferentialBusTrace.Compare(control, candidate);
            Assert.That(mismatch, Is.Not.Null, report);
            Assert.That(
                mismatch.Category,
                Is.EqualTo(fault == "order" ? "callbacks" : "state"),
                report
            );
            CollectionAssert.AreEqual(
                control.Select(item => item.Exception),
                candidate.Select(item => item.Exception),
                report
            );
            if (fault == "exception-cleanup")
            {
                Assert.That(
                    control[2].Exception,
                    Is.EqualTo(
                        "System.InvalidOperationException: intentional trace callback failure"
                    ),
                    report
                );
                Assert.That(control[3].Callbacks, Is.Empty, report);
                CollectionAssert.AreEqual(
                    new[] { "token=0,value=17" },
                    candidate[3].Callbacks,
                    report
                );
            }
            else
            {
                Assert.That(control.All(item => item.Exception == null), Is.True, report);
            }
            if (fault == "order")
            {
                CollectionAssert.AreEqual(
                    new[] { "token=0,value=17", "token=1,value=17" },
                    control[3].Callbacks,
                    report
                );
                CollectionAssert.AreEqual(
                    new[] { "token=1,value=17", "token=0,value=17" },
                    candidate[3].Callbacks,
                    report
                );
                CollectionAssert.AreEqual(
                    control.Select(item => item.State),
                    candidate.Select(item => item.State),
                    report
                );
            }
            if (fault == "diagnostics")
            {
                CollectionAssert.AreEqual(control[3].Callbacks, candidate[3].Callbacks, report);
                StringAssert.Contains("tokenMetadataCallsHistory=1/1/1,", control[3].State, report);
                StringAssert.Contains(
                    "tokenMetadataCallsHistory=1/2/2,",
                    candidate[3].State,
                    report
                );
            }
            if (fault == "retention")
            {
                CollectionAssert.AreEqual(control[4].Callbacks, candidate[4].Callbacks, report);
                StringAssert.Contains("tokenMetadataCallsHistory=0/0/0,", control[4].State, report);
                StringAssert.Contains(
                    "tokenMetadataCallsHistory=0/0/1,",
                    candidate[4].State,
                    report
                );
                StringAssert.Contains("retainedMessages=0,", control[4].State, report);
                StringAssert.Contains("retainedMessages=1,", candidate[4].State, report);
            }
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                original,
                sequence => EvaluateMutant(sequence, fault)
            );
            BusTraceMismatch replayed = EvaluateMutant(minimal, fault);
            report += "\nMinimized:\n" + replayed?.BuildReport(minimal);
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(replayed?.Category, Is.EqualTo(mismatch.Category), report);
            Assert.That(minimal.Version, Is.EqualTo(original.Version), report);
            Assert.That(minimal.Seed, Is.EqualTo(original.Seed), report);
            BusTraceOperationKind[] expected = fault switch
            {
                "order" => new[]
                {
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.Emit,
                },
                "stale" => new[]
                {
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.Remove,
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.RemoveStale,
                },
                "exception-cleanup" => new[]
                {
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.EmitWithThrow,
                },
                "diagnostics" => new[]
                {
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.SetDiagnostics,
                    BusTraceOperationKind.Emit,
                },
                "retention" => new[]
                {
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.SetDiagnostics,
                    BusTraceOperationKind.Emit,
                    BusTraceOperationKind.Remove,
                },
                _ => new[] { BusTraceOperationKind.Register, BusTraceOperationKind.Remove },
            };
            CollectionAssert.AreEqual(
                expected,
                minimal.Operations.Select(item => item.Kind),
                report
            );
            Assert.That(Evaluate(original, dropEmits: false), Is.Null, report);
            Assert.That(Evaluate(minimal, dropEmits: false), Is.Null, report);
        }

        [Test]
        public void VersionThreeValidityRequiresStaleHandleAndCallbackDependencies()
        {
            MessageScenario scenario = MessageScenario.Untargeted();
            BusTraceOperation[] operations =
            {
                new(BusTraceOperationKind.Register),
                new(BusTraceOperationKind.Remove),
                new(BusTraceOperationKind.Register),
                new(BusTraceOperationKind.RemoveStale),
                new(BusTraceOperationKind.SetDiagnostics, value: 1),
                new(BusTraceOperationKind.EmitWithThrow),
            };
            Assert.That(
                DifferentialBusTrace.IsValid(new BusTraceSequence(scenario, 509, operations)),
                Is.True,
                "Version 3 must accept stale cleanup after handle reuse and throwing callbacks."
            );
            foreach (int version in new[] { 1, 2 })
            {
                Assert.That(
                    DifferentialBusTrace.IsValid(
                        new BusTraceSequence(scenario, 509, operations, version)
                    ),
                    Is.False,
                    $"version={version}: older schemas cannot claim new operation semantics."
                );
            }
            BusTraceSequence generated = DifferentialBusTrace.Generate(scenario, 17, 256);
            foreach (
                BusTraceOperationKind kind in new[]
                {
                    BusTraceOperationKind.RemoveStale,
                    BusTraceOperationKind.EmitWithThrow,
                    BusTraceOperationKind.SetDiagnostics,
                }
            )
            {
                Assert.That(
                    generated.Operations.Any(operation => operation.Kind == kind),
                    Is.True,
                    $"Version 3 generation must exercise {kind}, not only hand-written fixtures."
                );
            }
            foreach (
                BusTraceOperation operation in new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.RemoveStale),
                    new BusTraceOperation(BusTraceOperationKind.EmitWithThrow),
                    new BusTraceOperation(BusTraceOperationKind.SetDiagnostics, value: -1),
                    new BusTraceOperation(BusTraceOperationKind.SetDiagnostics, value: 2),
                }
            )
            {
                Assert.That(
                    DifferentialBusTrace.IsValid(
                        new BusTraceSequence(scenario, 509, new[] { operation })
                    ),
                    Is.False,
                    $"{operation}: missing dependencies and invalid diagnostics settings must be rejected."
                );
            }
        }

        private static BusTraceMismatch EvaluateMutant(BusTraceSequence sequence, string fault) =>
            DifferentialBusTrace.Compare(
                DifferentialBusTrace.Replay(sequence, kind => CreateAdapter(kind, false)),
                DifferentialBusTrace.Replay(sequence, kind => CreateMutant(kind, fault))
            );

        private static MessageBusTraceAdapter CreateMutant(MessageScenario scenario, string fault)
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionEnabled: false
            );
            bus.DiagnosticsMode = false;
            return fault switch
            {
                "order" => new EqualPriorityReorderAdapter(scenario, bus),
                "stale" => new StaleHandleReuseAdapter(scenario, bus),
                "exception-cleanup" => new SkippedExceptionCleanupAdapter(scenario, bus),
                "diagnostics" => new DuplicateDiagnosticsAdapter(scenario, bus),
                "leak" => new RegistrationLeakAdapter(scenario, bus),
                "retention" => new RetainedDiagnosticReferenceAdapter(scenario, bus),
                _ => throw new ArgumentOutOfRangeException(nameof(fault)),
            };
        }

        // Each mutant changes a real operation before the shared observer reads production state.
        // None rewrites observations or implements message routing.
        private sealed class EqualPriorityReorderAdapter : MessageBusTraceAdapter
        {
            internal EqualPriorityReorderAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void Register(BusTraceOperation operation)
            {
                base.Register(operation);
                if (operation.Token != 0 && Token(0).Enabled)
                {
                    // Reinsert the first registration behind its equal-priority peers.
                    Token(0).Disable();
                    Token(0).Enable();
                }
            }
        }

        private sealed class StaleHandleReuseAdapter : MessageBusTraceAdapter
        {
            internal StaleHandleReuseAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void Remove(BusTraceOperation operation) =>
                base.Remove(
                    operation.Kind == BusTraceOperationKind.RemoveStale
                        ? new BusTraceOperation(
                            BusTraceOperationKind.Remove,
                            token: operation.Token
                        )
                        : operation
                );
        }

        private sealed class SkippedExceptionCleanupAdapter : MessageBusTraceAdapter
        {
            internal SkippedExceptionCleanupAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void CleanupThrowingCallback(int slot) { }
        }

        private sealed class DuplicateDiagnosticsAdapter : MessageBusTraceAdapter
        {
            internal DuplicateDiagnosticsAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void OnCallback(int slot, IMessage message)
            {
                if (Token(slot).DiagnosticMode)
                {
                    // The token also records this emission after the callback returns.
                    Token(slot).RecordDiagnosticEmission(Handle(slot), Bus, message);
                }
            }
        }

        private sealed class RetainedDiagnosticReferenceAdapter : MessageBusTraceAdapter
        {
            internal RetainedDiagnosticReferenceAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void Remove(BusTraceOperation operation)
            {
                MessageRegistrationToken token = Token(operation.Token);
                bool hasEmission = token._emissionBuffer.Count > 0;
                MessageEmissionData retained = hasEmission ? token._emissionBuffer[0] : default;
                base.Remove(operation);
                if (hasEmission && token._metadata.Count == 0)
                {
                    // Preserve the actual boxed message reference after its registration dies.
                    // This mutates the production holder, not the recorded observation.
                    token._emissionBuffer.Add(retained);
                }
            }
        }

        private sealed class RegistrationLeakAdapter : MessageBusTraceAdapter
        {
            internal RegistrationLeakAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void Remove(BusTraceOperation operation) { }
        }

        private static BusTraceSequence ResetSequence(MessageScenario scenario) =>
            new(
                scenario,
                17,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Enable, token: 3),
                    new BusTraceOperation(BusTraceOperationKind.Register, priority: -1),
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 1, priority: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 11),
                    new BusTraceOperation(BusTraceOperationKind.EmitWithReset, value: 13),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 14),
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 2),
                    new BusTraceOperation(BusTraceOperationKind.Remove),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 15),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 2),
                }
            );

        private static BusTraceMismatch EvaluateDeferredReset(BusTraceSequence sequence) =>
            DifferentialBusTrace.Compare(
                DifferentialBusTrace.Replay(sequence, scenario => CreateAdapter(scenario, false)),
                DifferentialBusTrace.Replay(
                    sequence,
                    scenario => CreateAdapter(scenario, false, deferReset: true)
                )
            );

        private static string DescribeReplay(
            BusTraceSequence sequence,
            IReadOnlyList<BusTraceObservation> control,
            IReadOnlyList<BusTraceObservation> candidate
        ) =>
            $"[{sequence.Scenario.Kind}] "
            + (
                DifferentialBusTrace.Compare(control, candidate)?.BuildReport(sequence)
                ?? "Matching replays"
            )
            + "\nControl trace:\n"
            + string.Join("\n", control.Select((observation, index) => $"[{index}] {observation}"))
            + "\nCandidate trace:\n"
            + string.Join(
                "\n",
                candidate.Select((observation, index) => $"[{index}] {observation}")
            );

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
                    "generator=3",
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
            bool dropEmits,
            bool deferReset = false
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionEnabled: false
            );
            bus.DiagnosticsMode = false;
            DeferredResetEmitter delayed = deferReset ? new DeferredResetEmitter(bus) : null;
            return new MessageBusTraceAdapter(
                scenario,
                bus,
                dropEmits ? new DropEmitsBus(bus) : delayed,
                delayed != null ? delayed.RequestReset : bus.ResetState
            );
        }

        /// <summary>Intentional mutant: postpones a real reset request until the enclosing emission returns.</summary>
        private sealed class DeferredResetEmitter : DelegatingMessageBus
        {
            private readonly MessageBus _bus;
            private bool _pending;

            internal DeferredResetEmitter(MessageBus bus)
                : base(bus)
            {
                _bus = bus;
            }

            internal void RequestReset() => _pending = true;

            private void FlushReset()
            {
                if (_pending)
                {
                    _pending = false;
                    _bus.ResetState();
                }
            }

            public override void UntargetedBroadcast<TMessage>(ref TMessage message)
            {
                try
                {
                    base.UntargetedBroadcast(ref message);
                }
                finally
                {
                    FlushReset();
                }
            }

            public override void TargetedBroadcast<TMessage>(
                ref InstanceId target,
                ref TMessage message
            )
            {
                try
                {
                    base.TargetedBroadcast(ref target, ref message);
                }
                finally
                {
                    FlushReset();
                }
            }

            public override void SourcedBroadcast<TMessage>(
                ref InstanceId source,
                ref TMessage message
            )
            {
                try
                {
                    base.SourcedBroadcast(ref source, ref message);
                }
                finally
                {
                    FlushReset();
                }
            }
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
