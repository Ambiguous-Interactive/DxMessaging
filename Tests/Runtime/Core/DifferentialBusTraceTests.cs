#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
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
        public void EmissionObservationScopesRestoreGlobalsAfterCompletionThrowResetAndNestedUnmatched(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values("Emit", "EmitWithThrow", "EmitWithReset", "EmitNested", "NestedThrow")]
                string operationName,
            [Values(false, true)] bool initiallyEnabled
        )
        {
            BusTraceOperationKind operationKind =
                operationName == "NestedThrow"
                    ? BusTraceOperationKind.EmitNested
                    : (BusTraceOperationKind)
                        Enum.Parse(typeof(BusTraceOperationKind), operationName);
            bool savedEnabled = MessagingDebug.enabled;
            Action<LogLevel, string> savedLog = MessagingDebug.LogFunction;
            Action<LogLevel, string> sentinel = (_, _) =>
                Assert.Fail("An emission escaped its scoped collector.");
            string report =
                $"kind={scenario.Kind}, operation={operationName}, initiallyEnabled={initiallyEnabled}";
            try
            {
                MessagingDebug.enabled = initiallyEnabled;
                MessagingDebug.LogFunction = sentinel;
                using MessageBusTraceAdapter adapter = CreateAdapter(
                    scenario,
                    false,
                    throwAfterEmit: operationName == "NestedThrow"
                );
                adapter.Execute(new BusTraceOperation(BusTraceOperationKind.Register));
                adapter.Execute(
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        token: 1,
                        context: 1,
                        kindOffset: 1
                    )
                );
                adapter.Execute(new BusTraceOperation(BusTraceOperationKind.Disable, token: 1));
                BusTraceObservation observation = adapter.Execute(
                    new BusTraceOperation(operationKind, value: 11, nestedToken: 1, depth: 1)
                );
                Assert.That(MessagingDebug.enabled, Is.EqualTo(initiallyEnabled), report);
                Assert.That(MessagingDebug.LogFunction, Is.SameAs(sentinel), report);
                Assert.That(
                    observation.Exception != null,
                    Is.EqualTo(
                        operationKind == BusTraceOperationKind.EmitWithThrow
                            || operationName == "NestedThrow"
                    ),
                    report + observation
                );
                int expectedFinalCount = operationKind == BusTraceOperationKind.EmitNested ? 2 : 1;
                Assert.That(
                    observation.FinalEmissions.Count,
                    Is.EqualTo(expectedFinalCount),
                    report + observation
                );
                string context = scenario.Kind == MessageKind.Untargeted ? "none" : "2000";
                Assert.That(
                    observation.FinalEmissions[expectedFinalCount - 1],
                    Is.EqualTo($"call=0,kind={scenario.Kind},value=11,context={context}"),
                    report + observation
                );
                if (operationKind == BusTraceOperationKind.EmitNested)
                {
                    MessageKind inner = (MessageKind)(((int)scenario.Kind + 1) % 3);
                    string innerContext = inner == MessageKind.Untargeted ? "none" : "2001";
                    Assert.That(
                        observation.FinalEmissions[0],
                        Is.EqualTo($"call=1,kind={inner},value=12,context={innerContext}"),
                        report + observation
                    );
                    Assert.That(
                        observation.UnmatchedDiagnostics.Count,
                        Is.EqualTo(1),
                        report + observation
                    );
                    StringAssert.StartsWith("call=1;", observation.UnmatchedDiagnostics[0], report);
                }
                // A later unmatched call gets a fresh ordinal and cannot mutate the prior snapshot.
                BusTraceObservation later = adapter.Execute(
                    new BusTraceOperation(
                        BusTraceOperationKind.Emit,
                        value: 31,
                        context: 2,
                        kindOffset: 2
                    )
                );
                Assert.That(later.FinalEmissions.Count, Is.EqualTo(1), report + later);
                StringAssert.StartsWith("call=0,", later.FinalEmissions[0], report);
                Assert.That(later.UnmatchedDiagnostics.Count, Is.EqualTo(1), report + later);
                StringAssert.StartsWith("call=0;", later.UnmatchedDiagnostics[0], report);
                CollectionAssert.Contains(
                    observation.FinalEmissions,
                    $"call=0,kind={scenario.Kind},value=11,context={context}",
                    report + observation
                );
                if (operationKind == BusTraceOperationKind.EmitNested)
                {
                    StringAssert.StartsWith("call=1;", observation.UnmatchedDiagnostics[0], report);
                }
                Assert.That(MessagingDebug.enabled, Is.EqualTo(initiallyEnabled), report);
                Assert.That(MessagingDebug.LogFunction, Is.SameAs(sentinel), report);
            }
            finally
            {
                MessagingDebug.LogFunction = savedLog;
                MessagingDebug.enabled = savedEnabled;
            }
        }

        [Test]
        public void MissingNestedUnmatchedEmitIsDetectedWithoutChangingCallbacks(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                641,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        token: 1,
                        context: 1,
                        kindOffset: 1
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Disable, token: 1),
                    new BusTraceOperation(
                        BusTraceOperationKind.EmitNested,
                        value: 11,
                        nestedToken: 1,
                        depth: 1
                    ),
                }
            );
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateMutant(kind, "nested")
            );
            string report = DescribeReplay(sequence, control, candidate);
            CollectionAssert.AreEqual(control[3].Callbacks, candidate[3].Callbacks, report);
            Assert.That(candidate[3].State, Is.EqualTo(control[3].State), report);
            Assert.That(control[3].FinalEmissions.Count, Is.EqualTo(2), report);
            Assert.That(candidate[3].FinalEmissions.Count, Is.EqualTo(1), report);
            Assert.That(control[3].UnmatchedDiagnostics.Count, Is.EqualTo(1), report);
            Assert.That(candidate[3].UnmatchedDiagnostics, Is.Empty, report);
            Assert.That(
                DifferentialBusTrace.Compare(control, candidate)?.Category,
                Is.EqualTo("final-emission"),
                report
            );
        }

        [Test]
        public void UnmatchedDiagnosticReportsAreIndependentOfCallbackCountsAndVetoes(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values("empty", "global", "bare", "veto")] string setup
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionEnabled: false
            );
            using DiagnosticCalibrationAdapter adapter = new(scenario, bus);
            adapter.Configure(setup);
            Action<LogLevel, string> savedLog = MessagingDebug.LogFunction;
            List<(LogLevel, string)> forwarded = new();
            Action<LogLevel, string> original = (level, message) => forwarded.Add((level, message));
            try
            {
                MessagingDebug.LogFunction = original;
                BusTraceObservation observation = adapter.Execute(
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 17)
                );
                string report = $"kind={scenario.Kind}, setup={setup}: {observation}";
                Assert.That(observation.Exception, Is.Null, report);
                Assert.That(observation.Callbacks, Is.Empty, report);
                Assert.That(adapter.GlobalCalls, Is.EqualTo(setup == "global" ? 1 : 0), report);
                Assert.That(adapter.VetoCalls, Is.EqualTo(setup == "veto" ? 1 : 0), report);
                // Global-only delivery can log unmatched; a bare bus bucket can suppress it
                // without invoking a delegate; veto stops before any unmatched report.
                Assert.That(
                    observation.UnmatchedDiagnostics.Count,
                    Is.EqualTo(setup == "empty" || setup == "global" ? 1 : 0),
                    report
                );
                (LogLevel, string)[] expected =
                    setup == "global"
                        ? new[]
                        {
                            (LogLevel.Info, (string)null),
                            (LogLevel.Info, "unrelated {0} diagnostic"),
                            (LogLevel.Error, "unrelated error"),
                            (
                                LogLevel.Warn,
                                "Could not find a matching untargeted broadcast handler noise"
                            ),
                        }
                        : Array.Empty<(LogLevel, string)>();
                CollectionAssert.AreEqual(expected, forwarded, report);
                Assert.That(MessagingDebug.LogFunction, Is.SameAs(original), report);
            }
            finally
            {
                MessagingDebug.LogFunction = savedLog;
            }
        }

        [Test]
        public void TypedFinalPayloadObservationsDetectRealMutationAndShrink(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool throwAfterEmit
        )
        {
            VerifyFinalMutation(scenario, "payload", throwAfterEmit);
        }

        [Test]
        public void TypedFinalContextObservationsDetectRealMutationAndShrink(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            VerifyFinalMutation(scenario, "context", false);
        }

        private static void VerifyFinalMutation(
            MessageScenario scenario,
            string fault,
            bool throwAfterEmit
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                619,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 17),
                    new BusTraceOperation(BusTraceOperationKind.Remove),
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: 1),
                }
            );
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind =>
                    CreateAdapter(
                        kind,
                        false,
                        observationFault: null,
                        throwAfterEmit: throwAfterEmit
                    )
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                sequence,
                kind =>
                    CreateAdapter(
                        kind,
                        false,
                        observationFault: fault,
                        throwAfterEmit: throwAfterEmit
                    )
            );
            string report = DescribeReplay(sequence, control, candidate);
            CollectionAssert.AreEqual(control[1].Callbacks, candidate[1].Callbacks, report);
            Assert.That(candidate[1].State, Is.EqualTo(control[1].State), report);
            Assert.That(candidate[1].Exception, Is.EqualTo(control[1].Exception), report);
            Assert.That(control[1].Exception == null, Is.EqualTo(!throwAfterEmit), report);
            CollectionAssert.AreEqual(
                control[1].UnmatchedDiagnostics,
                candidate[1].UnmatchedDiagnostics,
                report
            );
            string context = scenario.Kind == MessageKind.Untargeted ? "none" : "2000";
            CollectionAssert.AreEqual(
                new[] { $"call=0,kind={scenario.Kind},value=17,context={context}" },
                control[1].FinalEmissions,
                report
            );
            CollectionAssert.AreEqual(
                new[]
                {
                    $"call=0,kind={scenario.Kind},value={(fault == "payload" ? 117 : 17)},context={(fault == "context" ? "2100" : context)}",
                },
                candidate[1].FinalEmissions,
                report
            );
            BusTraceMismatch EvaluateFinal(BusTraceSequence input) =>
                DifferentialBusTrace.Compare(
                    DifferentialBusTrace.Replay(
                        input,
                        kind => CreateAdapter(kind, false, throwAfterEmit: throwAfterEmit)
                    ),
                    DifferentialBusTrace.Replay(
                        input,
                        kind =>
                            CreateAdapter(
                                kind,
                                false,
                                observationFault: fault,
                                throwAfterEmit: throwAfterEmit
                            )
                    )
                );
            BusTraceMismatch mismatch = EvaluateFinal(sequence);
            Assert.That(mismatch?.Category, Is.EqualTo("final-emission"), report);
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(sequence, EvaluateFinal);
            Assert.That(minimal.Operations.Count, Is.EqualTo(1), report);
            Assert.That(minimal.Operations[0].Kind, Is.EqualTo(BusTraceOperationKind.Emit), report);
            Assert.That(minimal.Version, Is.EqualTo(sequence.Version), report);
            StringAssert.Contains("observationSchema=2", mismatch.BuildReport(sequence), report);
        }

        [Test]
        public void UnmatchedDiagnosticsDetectEmptyBusEmissionWithoutInventingFoundResult(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                631,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 29),
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: 1),
                }
            );
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, true)
            );
            string report = DescribeReplay(sequence, control, candidate);
            Assert.That(control[1].Callbacks, Is.Empty, report);
            Assert.That(candidate[1].Callbacks, Is.Empty, report);
            Assert.That(candidate[1].State, Is.EqualTo(control[1].State), report);
            CollectionAssert.AreEqual(
                control[1].FinalEmissions,
                candidate[1].FinalEmissions,
                report
            );
            Assert.That(control[1].UnmatchedDiagnostics.Count, Is.EqualTo(1), report);
            StringAssert.StartsWith(
                "call=0;Could not find a matching ",
                control[1].UnmatchedDiagnostics[0],
                report
            );
            Assert.That(candidate[1].UnmatchedDiagnostics, Is.Empty, report);
            Assert.That(
                Evaluate(sequence, true)?.Category,
                Is.EqualTo("unmatched-diagnostic"),
                report
            );
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                sequence,
                input => Evaluate(input, true)
            );
            Assert.That(minimal.Operations.Count, Is.EqualTo(1), report);
            Assert.That(minimal.Operations[0].Kind, Is.EqualTo(BusTraceOperationKind.Emit), report);
        }

        [Test]
        public void GeneratorVersionPinsKnownSeedPrefix(
            [Values(1, 2, 3, 4, 5, 6, 7, 8, 9, 10)] int version
        )
        {
            BusTraceSequence sequence = DifferentialBusTrace.Generate(
                MessageScenario.Untargeted(),
                17,
                4,
                generatorVersion: version
            );
            Assert.That(
                BusTraceSequence.GeneratorVersion,
                Is.EqualTo(10),
                "Changing generation requires a new version and a reviewed replay fixture."
            );
            CollectionAssert.AreEqual(
                version >= 8
                        ? new[]
                        {
                            "Register(token=0,context=0,value=0,priority=0)[handleSlot=0,sourceHandleSlot=-1]",
                            "DuplicateRegistration(token=0,context=0,value=0,priority=0)[handleSlot=1,sourceHandleSlot=0]",
                            "CopyHandle(token=0,context=0,value=0,priority=0)[handleSlot=2,sourceHandleSlot=0]",
                            "Emit(token=0,context=0,value=0,priority=0)",
                        }
                    : version == 7
                        ? new[]
                        {
                            "Register(token=0,context=0,value=1409999377,priority=1)",
                            "Emit(token=0,context=0,value=1481184789,priority=1)",
                            "Emit(token=3,context=1,value=-541008231,priority=1)",
                            "Remove(token=0,context=1,value=-1592061232,priority=1)",
                        }
                    : version == 6
                        ? new[]
                        {
                            "Register(token=0,context=0,value=1409999377,priority=1)",
                            "Emit(token=0,context=0,value=1481184789,priority=1)",
                            "Emit(token=3,context=1,value=-541008231,priority=1)",
                            "EmitNested(token=0,context=0,value=-1592061232,priority=0)[kindOffset=0,nestedToken=0,depth=1]",
                        }
                    : version == 5
                        ? new[]
                        {
                            "Register(token=0,context=0,value=1409999377,priority=1)",
                            "Emit(token=0,context=0,value=1481184789,priority=1)",
                            "Register(token=3,context=1,value=-541008231,priority=1)",
                            "EmitNested(token=0,context=0,value=-1592061232,priority=0)[kindOffset=0,nestedToken=0,depth=1]",
                        }
                    : new[]
                    {
                        "Register(token=0,context=0,value=1409999377,priority=1)",
                        "Emit(token=0,context=0,value=-576100708,priority=-1)",
                        (
                            version == 1 ? "Disable"
                            : version == 2 ? "Emit"
                            : version == 3 ? "Register"
                            : "SetDiagnostics"
                        )
                            + $"(token=1,context=0,value={(version == 4 ? 0 : 1944224582)},priority=1)",
                        (
                            version == 1 ? "Disable"
                            : version == 2 ? "Register"
                            : version == 3 ? "Emit"
                            : "Disable"
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
        public void GlobalOverrideReplayPreservesCopiesOutOfOrderDisposalReuseAndReplacement(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(
                        BusTraceOperationKind.AcquireGlobalOverride,
                        context: 1,
                        leaseSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyGlobalOverride,
                        leaseSlot: 1,
                        sourceLeaseSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.AcquireGlobalOverride,
                        leaseSlot: 2
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 1
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 11),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 2
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.AcquireGlobalOverride,
                        context: 1,
                        leaseSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 1
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 13),
                    new BusTraceOperation(BusTraceOperationKind.ReplaceGlobalBus),
                    new BusTraceOperation(
                        BusTraceOperationKind.AcquireGlobalOverride,
                        context: 1,
                        leaseSlot: 2
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 17),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 2
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 19),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 2
                    ),
                }
            );
            IMessageBus original = MessageHandler.MessageBus;
            IReadOnlyList<BusTraceObservation> observations = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            string report =
                $"[{scenario.Kind}] generator={sequence.Version}, seed={sequence.Seed}\n"
                + string.Join("\n", observations);
            Assert.That(MessageHandler.MessageBus, Is.SameAs(original), report);
            Assert.That(observations.All(item => item.Exception == null), Is.True, report);
            Assert.That(
                DifferentialBusTrace.Compare(
                    observations,
                    DifferentialBusTrace.Replay(sequence, kind => CreateAdapter(kind, false))
                ),
                Is.Null,
                report
            );
            foreach (int index in new[] { 5, 15 })
            {
                CollectionAssert.AreEqual(
                    new[] { $"token=0,value={sequence.Operations[index].Value}" },
                    observations[index].Callbacks,
                    report
                );
                StringAssert.Contains("globalBus=primary", observations[index].State, report);
            }
            foreach (int index in new[] { 9, 13 })
            {
                Assert.That(observations[index].Callbacks, Is.Empty, report);
                Assert.That(observations[index].UnmatchedDiagnostics.Count, Is.EqualTo(1), report);
                StringAssert.Contains("globalBus=alternate", observations[index].State, report);
            }
            StringAssert.Contains("globalBus=primary", observations[6].State, report);
            StringAssert.Contains("globalBus=primary", observations[16].State, report);
        }

        [Test]
        public void GlobalOverrideReplayPreservesSelectedPrimaryEmitterMutations(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values("drop", "payload", "deferred-reset")] string mutation
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                271,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 1),
                    new BusTraceOperation(
                        BusTraceOperationKind.AcquireGlobalOverride,
                        context: 1,
                        leaseSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 11),
                    new BusTraceOperation(
                        BusTraceOperationKind.AcquireGlobalOverride,
                        leaseSlot: 1
                    ),
                    new BusTraceOperation(BusTraceOperationKind.EmitWithReset, value: 13),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 1
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 17),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 19),
                }
            );
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                sequence,
                kind =>
                    CreateAdapter(
                        kind,
                        mutation == "drop",
                        deferReset: mutation == "deferred-reset",
                        observationFault: mutation == "payload" ? "payload" : null
                    )
            );
            string report = $"mutation={mutation}\n" + DescribeReplay(sequence, control, candidate);
            Assert.That(DifferentialBusTrace.IsValid(sequence), Is.True, report);
            Assert.That(control.All(item => item.Exception == null), Is.True, report);
            Assert.That(candidate.All(item => item.Exception == null), Is.True, report);
            Assert.That(
                DifferentialBusTrace.Compare(
                    control.Take(5).ToArray(),
                    candidate.Take(5).ToArray()
                ),
                Is.Null,
                "An alternate global bus must not use the primary emitter. " + report
            );
            foreach (int index in new[] { 3, 7 })
            {
                Assert.That(candidate[index].Callbacks, Is.Empty, report);
                StringAssert.Contains("globalBus=alternate", candidate[index].State, report);
            }
            foreach (int index in new[] { 5, 9 })
            {
                StringAssert.Contains("globalBus=primary", candidate[index].State, report);
            }
            BusTraceMismatch mismatch = DifferentialBusTrace.Compare(control, candidate);
            Assert.That(mismatch?.Index, Is.EqualTo(5), report);
            Assert.That(
                mismatch?.Category,
                Is.EqualTo(mutation == "payload" ? "final-emission" : "callbacks"),
                report
            );
            Assert.That(
                candidate[5].Callbacks.Count,
                Is.EqualTo(
                    mutation == "drop" ? 0
                    : mutation == "deferred-reset" ? 2
                    : 1
                ),
                report
            );
            if (mutation == "deferred-reset")
            {
                Assert.That(candidate[5].State, Is.EqualTo(control[5].State), report);
                Assert.That(candidate[9].Callbacks, Is.Empty, report);
            }
        }

        [Test]
        public void GlobalOverrideAliasMutationIsDetectedAndShrunk(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Enable, token: 3),
                    new BusTraceOperation(
                        BusTraceOperationKind.AcquireGlobalOverride,
                        context: 1,
                        leaseSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyGlobalOverride,
                        leaseSlot: 1,
                        sourceLeaseSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.AcquireGlobalOverride,
                        context: 1,
                        leaseSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 1
                    ),
                }
            );
            BusTraceMismatch EvaluateOverride(BusTraceSequence input) =>
                DifferentialBusTrace.Compare(
                    DifferentialBusTrace.Replay(input, kind => CreateAdapter(kind, false)),
                    DifferentialBusTrace.Replay(
                        input,
                        kind => new GlobalOverrideFaultAdapter(kind, wrongAlias: true)
                    )
                );
            BusTraceMismatch mismatch = EvaluateOverride(sequence);
            Assert.That(
                mismatch,
                Is.Not.Null,
                $"[{scenario.Kind}] stale copy disposal must expose a generation ownership fault."
            );
            string report = mismatch.BuildReport(sequence);
            Assert.That(mismatch.Category, Is.EqualTo("state"), report);
            Assert.That(mismatch.Index, Is.EqualTo(5), report);
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(sequence, EvaluateOverride);
            Assert.That(minimal.Version, Is.EqualTo(10), report);
            Assert.That(minimal.Operations.Count, Is.EqualTo(5), report);
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(EvaluateOverride(minimal)?.Category, Is.EqualTo("state"), report);
            for (int index = 0; index < minimal.Operations.Count; ++index)
            {
                BusTraceSequence deletion = new(
                    scenario,
                    minimal.Seed,
                    minimal.Operations.Where((_, position) => position != index),
                    minimal.Version
                );
                Assert.That(
                    !DifferentialBusTrace.IsValid(deletion) || EvaluateOverride(deletion) == null,
                    Is.True,
                    report + $"\ndeleted={index}"
                );
            }
        }

        [Test]
        public void GlobalOverrideGenerationRetainsAllOperationsAndValidPrefixes(
            [Values(0, 17, 42)] int seed
        )
        {
            MessageScenario scenario = MessageScenario.Untargeted();
            BusTraceSequence sequence = DifferentialBusTrace.Generate(scenario, (uint)seed, 64, 10);
            string report = $"seed={seed}, version=10";
            foreach (
                BusTraceOperationKind kind in new[]
                {
                    BusTraceOperationKind.AcquireGlobalOverride,
                    BusTraceOperationKind.CopyGlobalOverride,
                    BusTraceOperationKind.DisposeGlobalOverride,
                    BusTraceOperationKind.ReplaceGlobalBus,
                }
            )
            {
                Assert.That(
                    sequence.Operations.Any(operation => operation.Kind == kind),
                    Is.True,
                    report + $", operation={kind}"
                );
            }
            for (int length = 0; length <= sequence.Operations.Count; ++length)
            {
                BusTraceSequence prefix = new(
                    scenario,
                    (uint)seed,
                    sequence.Operations.Take(length),
                    10
                );
                Assert.That(
                    DifferentialBusTrace.IsValid(prefix),
                    Is.True,
                    report + $", length={length}"
                );
            }
        }

        [Test]
        public void GlobalOverrideValidationRejectsMissingAndOverwrittenDependencies(
            [Values(
                "missing-copy",
                "missing-dispose",
                "overwrite-live",
                "copy-over-live",
                "invalid-slot",
                "invalid-source",
                "foreign-fields",
                "legacy-version"
            )]
                string fault
        )
        {
            BusTraceOperation acquire = new(
                BusTraceOperationKind.AcquireGlobalOverride,
                leaseSlot: 0
            );
            BusTraceOperation copy = new(
                BusTraceOperationKind.CopyGlobalOverride,
                leaseSlot: 1,
                sourceLeaseSlot: 0
            );
            BusTraceOperation[] operations = fault switch
            {
                "missing-copy" => new[] { copy },
                "missing-dispose" => new[]
                {
                    new BusTraceOperation(
                        BusTraceOperationKind.DisposeGlobalOverride,
                        leaseSlot: 0
                    ),
                },
                "overwrite-live" => new[] { acquire, acquire },
                "copy-over-live" => new[] { acquire, copy, copy },
                "invalid-slot" => new[]
                {
                    new BusTraceOperation(
                        BusTraceOperationKind.AcquireGlobalOverride,
                        leaseSlot: BusTraceSequence.TokenCount
                    ),
                },
                "invalid-source" => new[]
                {
                    acquire,
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyGlobalOverride,
                        leaseSlot: 1,
                        sourceLeaseSlot: BusTraceSequence.TokenCount
                    ),
                },
                "foreign-fields" => new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Emit, leaseSlot: 0),
                },
                _ => new[] { acquire },
            };
            BusTraceSequence sequence = new(
                MessageScenario.Untargeted(),
                509,
                operations,
                fault == "legacy-version" ? 9 : 10
            );
            Assert.That(
                DifferentialBusTrace.IsValid(sequence),
                Is.False,
                $"fault={fault}: invalid lease dependencies must not reach production replay."
            );
        }

        [Test]
        public void GlobalOverrideReplayRestoresOriginalBusWhenTokenCleanupFails(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            IMessageBus original = MessageHandler.MessageBus;
            GlobalOverrideFaultAdapter adapter = new(scenario, failCleanup: true);
            adapter.Execute(new BusTraceOperation(BusTraceOperationKind.Register));
            adapter.Execute(
                new BusTraceOperation(
                    BusTraceOperationKind.AcquireGlobalOverride,
                    context: 1,
                    leaseSlot: 0
                )
            );
            AggregateException error = Assert.Throws<AggregateException>(
                adapter.Dispose,
                $"[{scenario.Kind}] cleanup faults must be reported."
            );
            StringAssert.Contains(
                "intentional override cleanup failure",
                error.ToString(),
                $"[{scenario.Kind}] original cleanup error must survive."
            );
            Assert.That(
                MessageHandler.MessageBus,
                Is.SameAs(original),
                $"[{scenario.Kind}] cleanup failures must restore global routing."
            );
            Assert.That(
                adapter.CleanedTokens,
                Is.EqualTo(BusTraceSequence.TokenCount),
                $"[{scenario.Kind}] all tokens must be cleaned despite a prior failure."
            );
        }

        private sealed class GlobalOverrideFaultAdapter : MessageBusTraceAdapter
        {
            private readonly bool _wrongAlias;
            private readonly bool _failCleanup;
            internal int CleanedTokens { get; private set; }

            internal GlobalOverrideFaultAdapter(
                MessageScenario scenario,
                bool wrongAlias = false,
                bool failCleanup = false
            )
                : base(scenario, NewBus())
            {
                _wrongAlias = wrongAlias;
                _failCleanup = failCleanup;
            }

            private static MessageBus NewBus()
            {
                MessageBus bus = MessageBus.CreateForInternalUse(
                    new FakeClock(),
                    idleEvictionTicks: 0,
                    idleEvictionEnabled: false,
                    trimApiEnabled: true
                );
                bus.DiagnosticsMode = false;
                return bus;
            }

            protected override void DisposeGlobalOverride(int slot) =>
                base.DisposeGlobalOverride(_wrongAlias && slot == 1 ? 0 : slot);

            protected override void DisposeToken(int slot)
            {
                base.DisposeToken(slot);
                ++CleanedTokens;
                if (_failCleanup && slot == 0)
                {
                    throw new InvalidOperationException("intentional override cleanup failure");
                }
            }
        }

        [Test]
        public void IndependentHandlesPreserveDuplicatesCopiesAndOutOfOrderRemoval(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool removeCopy,
            [Values(false, true)] bool duplicateFirst
        )
        {
            int original = removeCopy ? 2 : 0;
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Enable),
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        token: 3,
                        context: 1,
                        priority: -1,
                        handleSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.DuplicateRegistration,
                        token: 3,
                        handleSlot: 1,
                        sourceHandleSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyHandle,
                        token: 3,
                        handleSlot: 2,
                        sourceHandleSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        token: 3,
                        context: 1,
                        priority: 1,
                        handleSlot: 3
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, context: 1, value: 11),
                    new BusTraceOperation(
                        BusTraceOperationKind.Remove,
                        token: 3,
                        handleSlot: duplicateFirst ? 1 : original
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, context: 1, value: 13),
                    new BusTraceOperation(
                        BusTraceOperationKind.Remove,
                        token: 3,
                        handleSlot: duplicateFirst ? original : 1
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, context: 1, value: 17),
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        token: 3,
                        context: 1,
                        priority: -1,
                        handleSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 3, handleSlot: 2),
                    new BusTraceOperation(BusTraceOperationKind.Emit, context: 1, value: 19),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 3, handleSlot: 3),
                    new BusTraceOperation(BusTraceOperationKind.Emit, context: 1, value: 23),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 3, handleSlot: 0),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 3, handleSlot: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, context: 1, value: 29),
                }
            );
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            string report =
                $"removeCopy={removeCopy}; duplicateFirst={duplicateFirst}; "
                + DescribeReplay(sequence, control, candidate);
            Assert.That(control.All(item => item.Exception == null), Is.True, report);
            Assert.That(DifferentialBusTrace.Compare(control, candidate), Is.Null, report);
            foreach (int index in new[] { 5, 7, 12 })
            {
                int value = sequence.Operations[index].Value;
                CollectionAssert.AreEqual(
                    new[]
                    {
                        $"token=3,value={value},registration=0,callback={(index == 12 ? 2 : 0)}",
                        $"token=3,value={value},registration=3,callback=1",
                    },
                    control[index].Callbacks,
                    report
                );
            }
            CollectionAssert.AreEqual(
                new[] { "token=3,value=17,registration=3,callback=1" },
                control[9].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=3,value=23,registration=0,callback=2" },
                control[14].Callbacks,
                report
            );
            Assert.That(control[17].Callbacks, Is.Empty, report);
            StringAssert.Contains(
                "tokenMetadataCallsHistory=0/0/0,0/0/0,0/0/0,3/0/0,",
                control[5].State,
                report
            );
        }

        [Test]
        public void IndependentHandleMutantsAreDetectedAndShrunk(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool duplicateAsCopy
        )
        {
            List<BusTraceOperation> operations = new()
            {
                new(BusTraceOperationKind.Enable, token: 3),
                new(BusTraceOperationKind.Register, handleSlot: 0),
            };
            if (duplicateAsCopy)
            {
                operations.Add(
                    new(
                        BusTraceOperationKind.DuplicateRegistration,
                        handleSlot: 1,
                        sourceHandleSlot: 0
                    )
                );
            }
            else
            {
                operations.Add(new(BusTraceOperationKind.Register, priority: 1, handleSlot: 1));
                operations.Add(
                    new(BusTraceOperationKind.CopyHandle, handleSlot: 2, sourceHandleSlot: 1)
                );
                operations.Add(new(BusTraceOperationKind.Remove, handleSlot: 2));
            }
            operations.Add(new(BusTraceOperationKind.Emit, value: 11));
            BusTraceSequence sequence = new(scenario, 509, operations);
            string fault = duplicateAsCopy ? "duplicate-as-copy" : "wrong-independent-handle";
            BusTraceMismatch mismatch = EvaluateMutant(sequence, fault);
            Assert.That(mismatch, Is.Not.Null, $"[{scenario.Kind}] {fault}");
            string report = mismatch.BuildReport(sequence);
            Assert.That(
                mismatch.Category,
                Is.EqualTo(duplicateAsCopy ? "state" : "callbacks"),
                report
            );
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                sequence,
                candidate => EvaluateMutant(candidate, fault)
            );
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(minimal.Operations.Count, Is.EqualTo(duplicateAsCopy ? 2 : 5), report);
            Assert.That(
                EvaluateMutant(minimal, fault)?.Category,
                Is.EqualTo(mismatch.Category),
                report
            );
            Assert.That(Evaluate(minimal, false), Is.Null, report);
            for (int index = 0; index < minimal.Operations.Count; ++index)
            {
                List<BusTraceOperation> remaining = new(minimal.Operations);
                remaining.RemoveAt(index);
                BusTraceSequence deletion = new(scenario, minimal.Seed, remaining, minimal.Version);
                Assert.That(
                    !DifferentialBusTrace.IsValid(deletion)
                        || EvaluateMutant(deletion, fault)?.Category != mismatch.Category,
                    Is.True,
                    $"{report}; deletion={index}"
                );
            }
        }

        [Test]
        public void ReusedHandleSlotKeepsOldDuplicateCallbackIdentityAndOrder(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register, handleSlot: 0),
                    new BusTraceOperation(
                        BusTraceOperationKind.DuplicateRegistration,
                        handleSlot: 1,
                        sourceHandleSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Remove, handleSlot: 0),
                    new BusTraceOperation(BusTraceOperationKind.Register, handleSlot: 0),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 11),
                }
            );
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateMutant(kind, "reused-callback-order")
            );
            string report = DescribeReplay(sequence, control, candidate);
            Assert.That(control.All(item => item.Exception == null), Is.True, report);
            Assert.That(candidate.All(item => item.Exception == null), Is.True, report);
            CollectionAssert.AreEqual(
                new[]
                {
                    "token=0,value=11,registration=0,callback=0",
                    "token=0,value=11,registration=0,callback=1",
                },
                control[4].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                control.Select(item => item.State),
                candidate.Select(item => item.State),
                report
            );
            CollectionAssert.AreEqual(
                control[4].Callbacks.Reverse(),
                candidate[4].Callbacks,
                report
            );
            BusTraceMismatch mismatch = DifferentialBusTrace.Compare(control, candidate);
            Assert.That(mismatch?.Category, Is.EqualTo("callbacks"), report);
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                sequence,
                replay => EvaluateMutant(replay, "reused-callback-order")
            );
            Assert.That(minimal.Operations.Count, Is.EqualTo(5), report);
            Assert.That(
                EvaluateMutant(minimal, "reused-callback-order")?.Category,
                Is.EqualTo("callbacks"),
                report
            );
        }

        [Test]
        public void LegacyCallbackCleanupTargetsItsRegistrationWithExplicitSiblings(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register, priority: 1),
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        priority: -1,
                        handleSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.EmitWithThrow, value: 11),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 13),
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyHandle,
                        handleSlot: 1,
                        sourceHandleSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Remove, handleSlot: 0),
                    new BusTraceOperation(BusTraceOperationKind.Remove),
                    new BusTraceOperation(BusTraceOperationKind.Register, priority: 1),
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        priority: -1,
                        handleSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Remove, handleSlot: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 17),
                }
            );
            IReadOnlyList<BusTraceObservation> observations = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            string report = $"[{scenario.Kind}] " + string.Join("\n", observations);
            CollectionAssert.AreEqual(
                new[] { "token=0,value=11,registration=0,callback=0", "token=0,value=11" },
                observations[2].Callbacks,
                report
            );
            StringAssert.Contains(
                "intentional trace callback failure",
                observations[2].Exception,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=13,registration=0,callback=0" },
                observations[3].Callbacks,
                report
            );
            Assert.That(observations[3].Exception, Is.Null, report);
            CollectionAssert.AreEqual(
                new[] { "token=0,value=17,registration=0,callback=1", "token=0,value=17" },
                observations[10].Callbacks,
                report
            );
            for (int index = 4; index < observations.Count; ++index)
            {
                Assert.That(observations[index].Exception, Is.Null, $"{report}; operation={index}");
            }
        }

        [Test]
        public void DefaultOperationPreservesLegacyHandleSemantics()
        {
            BusTraceOperation operation = default;
            Assert.That(
                operation.HandleSlot,
                Is.EqualTo(-1),
                "The default struct keeps the legacy token-associated handle."
            );
            Assert.That(
                operation.SourceHandleSlot,
                Is.EqualTo(-1),
                "The default struct must not acquire a copied-handle source."
            );
            Assert.That(
                operation.ToString(),
                Is.EqualTo(new BusTraceOperation(BusTraceOperationKind.Register).ToString()),
                "Default and explicitly constructed Register inputs must agree."
            );
            for (int version = 1; version <= BusTraceSequence.GeneratorVersion; ++version)
            {
                Assert.That(
                    DifferentialBusTrace.IsValid(
                        new(MessageScenario.Untargeted(), 509, new[] { operation }, version)
                    ),
                    Is.True,
                    $"version={version}: default Register remains supported."
                );
            }
        }

        [Test]
        public void IndependentHandleValidityRequiresIssuedOwnersAndPreservesAliases()
        {
            MessageScenario scenario = MessageScenario.Untargeted();
            BusTraceOperation register = new(
                BusTraceOperationKind.Register,
                token: 3,
                handleSlot: 0
            );
            BusTraceOperation copy = new(
                BusTraceOperationKind.CopyHandle,
                token: 3,
                handleSlot: 1,
                sourceHandleSlot: 0
            );
            BusTraceOperation remove = new(BusTraceOperationKind.Remove, token: 3, handleSlot: 1);
            BusTraceOperation duplicate = new(
                BusTraceOperationKind.DuplicateRegistration,
                token: 3,
                handleSlot: 2,
                sourceHandleSlot: 0
            );
            BusTraceOperation[][] invalid =
            {
                new[] { copy },
                new[] { duplicate },
                new[] { remove },
                new[] { register, register },
                new[] { register, copy, remove, duplicate },
                new[]
                {
                    register,
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyHandle,
                        handleSlot: 1,
                        sourceHandleSlot: 0
                    ),
                },
                new[]
                {
                    register,
                    new BusTraceOperation(BusTraceOperationKind.Remove, handleSlot: 0),
                },
                new[] { new BusTraceOperation(BusTraceOperationKind.Register, handleSlot: -2) },
                new[]
                {
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        handleSlot: BusTraceSequence.HandleSlotCount
                    ),
                },
                new[]
                {
                    register,
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyHandle,
                        token: 3,
                        handleSlot: 1,
                        sourceHandleSlot: BusTraceSequence.HandleSlotCount
                    ),
                },
                new[]
                {
                    register,
                    new BusTraceOperation(BusTraceOperationKind.Emit, handleSlot: 0),
                },
                new[] { new BusTraceOperation(BusTraceOperationKind.CopyHandle) },
            };
            foreach (BusTraceOperation[] operations in invalid)
            {
                Assert.That(
                    DifferentialBusTrace.IsValid(new(scenario, 509, operations)),
                    Is.False,
                    string.Join(";", operations)
                );
            }
            BusTraceSequence valid = new(
                scenario,
                509,
                new[]
                {
                    register,
                    copy,
                    remove,
                    register,
                    remove,
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyHandle,
                        token: 3,
                        handleSlot: 2,
                        sourceHandleSlot: 1
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 3, handleSlot: 2),
                }
            );
            Assert.That(
                DifferentialBusTrace.IsValid(valid),
                Is.True,
                "Copies retain their issued stale identity after another slot is reused."
            );
            Assert.That(
                DifferentialBusTrace.IsValid(new(scenario, 509, valid.Operations, 7)),
                Is.False,
                "Version seven cannot claim independent handle semantics."
            );
        }

        [Test]
        public void VersionEightGeneratesIndependentHandleOperationsAndPreservesLegacyCoverage()
        {
            HashSet<BusTraceOperationKind> kinds = new();
            for (uint seed = 0; seed < 32; ++seed)
            {
                BusTraceSequence sequence = DifferentialBusTrace.Generate(
                    MessageScenario.Untargeted(),
                    seed,
                    256,
                    8
                );
                Assert.That(
                    DifferentialBusTrace.IsValid(sequence),
                    Is.True,
                    $"seed={seed}: handle generation must preserve dependencies."
                );
                CollectionAssert.AreEqual(
                    sequence.Operations,
                    DifferentialBusTrace.Generate(sequence.Scenario, seed, 256, 8).Operations,
                    $"seed={seed}: generation must be deterministic."
                );
                foreach (BusTraceOperation operation in sequence.Operations)
                {
                    kinds.Add(operation.Kind);
                }
            }
            CollectionAssert.AreEquivalent(
                Enum.GetValues(typeof(BusTraceOperationKind))
                    .Cast<BusTraceOperationKind>()
                    .Where(kind => !DifferentialBusTrace.IsGlobalOverride(kind)),
                kinds,
                "Version eight must retain every existing operation and add duplicates/copies."
            );
        }

        [Test]
        public void ExplicitHandleCallbackActionsPreserveReusedIdentitiesAndShrinkMutants(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(
                "EmitWithReset",
                "EmitWithThrow",
                "EmitWithDisable",
                "EmitWithHandlerActive",
                "EmitNested"
            )]
                string action
        )
        {
            BusTraceOperationKind kind = (BusTraceOperationKind)
                Enum.Parse(typeof(BusTraceOperationKind), action);
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register, handleSlot: 0),
                    new BusTraceOperation(
                        BusTraceOperationKind.DuplicateRegistration,
                        handleSlot: 1,
                        sourceHandleSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Remove, handleSlot: 0),
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        priority: 1,
                        handleSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyHandle,
                        handleSlot: 2,
                        sourceHandleSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        token: 1,
                        context: 1,
                        kindOffset: 1,
                        handleSlot: 3
                    ),
                    new BusTraceOperation(
                        kind,
                        value: 11,
                        nestedToken: kind == BusTraceOperationKind.EmitNested ? 1 : 0,
                        depth: kind == BusTraceOperationKind.EmitNested ? 10 : 0,
                        handlerToken: kind == BusTraceOperationKind.EmitWithHandlerActive ? 1 : 0,
                        handleSlot: 2,
                        sourceHandleSlot: kind == BusTraceOperationKind.EmitNested ? 3 : -1
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Disable),
                    new BusTraceOperation(BusTraceOperationKind.Enable),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 17),
                    new BusTraceOperation(BusTraceOperationKind.Remove, handleSlot: 2),
                    new BusTraceOperation(BusTraceOperationKind.Remove, handleSlot: 2),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 19),
                }
            );
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                item => CreateAdapter(item, false)
            );
            string report = $"[{scenario.Kind}] action={action}\n" + string.Join("\n", control);
            CollectionAssert.AreEqual(
                new[]
                {
                    "token=0,value=11,registration=0,callback=0",
                    "token=0,value=11,registration=0,callback=1",
                },
                control[6].Callbacks.Take(2),
                report
            );
            Assert.That(
                control[6].Exception != null,
                Is.EqualTo(kind == BusTraceOperationKind.EmitWithThrow),
                report
            );
            CollectionAssert.AreEqual(
                kind == BusTraceOperationKind.EmitWithThrow
                    ? new[] { "token=0,value=17,registration=0,callback=0" }
                    : new[]
                    {
                        "token=0,value=17,registration=0,callback=0",
                        "token=0,value=17,registration=0,callback=1",
                    },
                control[9].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=19,registration=0,callback=0" },
                control[12].Callbacks,
                report
            );
            Assert.That(
                control.Where((_, index) => index != 6).All(item => item.Exception == null),
                Is.True,
                report
            );
            Assert.That(Evaluate(sequence, false), Is.Null, report);
            BusTraceMismatch mismatch = EvaluateMutant(sequence, "explicit-callback");
            Assert.That(mismatch, Is.Not.Null, report);
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                sequence,
                item => EvaluateMutant(item, "explicit-callback")
            );
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(minimal.Operations.Count, Is.LessThan(sequence.Operations.Count), report);
            Assert.That(minimal.Version, Is.EqualTo(sequence.Version), report);
            Assert.That(
                EvaluateMutant(minimal, "explicit-callback")?.Category,
                Is.EqualTo(mismatch.Category),
                report
            );
            Assert.That(Evaluate(minimal, false), Is.Null, report);
            for (int index = 0; index < minimal.Operations.Count; ++index)
            {
                List<BusTraceOperation> remaining = new(minimal.Operations);
                remaining.RemoveAt(index);
                BusTraceSequence deletion = new(scenario, minimal.Seed, remaining, minimal.Version);
                Assert.That(
                    !DifferentialBusTrace.IsValid(deletion)
                        || EvaluateMutant(deletion, "explicit-callback")?.Category
                            != mismatch.Category,
                    Is.True,
                    $"{report}; deletion={index}"
                );
            }
        }

        [Test]
        public void ExplicitThrowCleanupLeavesDuplicateUsableAndStaleAliasCannotRearm(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register, handleSlot: 0),
                    new BusTraceOperation(
                        BusTraceOperationKind.DuplicateRegistration,
                        handleSlot: 1,
                        sourceHandleSlot: 0
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.CopyHandle,
                        handleSlot: 2,
                        sourceHandleSlot: 1
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.EmitWithThrow,
                        value: 11,
                        handleSlot: 2
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.EmitWithThrow,
                        value: 13,
                        handleSlot: 2
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.EmitWithThrow,
                        value: 17,
                        handleSlot: 0
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Remove, handleSlot: 0),
                    new BusTraceOperation(BusTraceOperationKind.Register, handleSlot: 0),
                    new BusTraceOperation(
                        BusTraceOperationKind.EmitWithThrow,
                        value: 19,
                        handleSlot: 2
                    ),
                }
            );
            IReadOnlyList<BusTraceObservation> observations = DifferentialBusTrace.Replay(
                sequence,
                item => CreateAdapter(item, false)
            );
            string report = $"[{scenario.Kind}] " + string.Join("\n", observations);
            for (int index = 0; index < observations.Count; ++index)
            {
                Assert.That(
                    observations[index].Exception != null,
                    Is.EqualTo(index == 3 || index == 5),
                    $"{report}; operation={index}"
                );
            }
            CollectionAssert.AreEqual(
                new[] { "token=0,value=13,registration=0,callback=0" },
                observations[4].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=19,registration=0,callback=1" },
                observations[8].Callbacks,
                report
            );
            Assert.That(Evaluate(sequence, false), Is.Null, report);
            BusTraceMismatch mismatch = EvaluateMutant(sequence, "explicit-cleanup");
            Assert.That(mismatch, Is.Not.Null, report);
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                sequence,
                item => EvaluateMutant(item, "explicit-cleanup")
            );
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(minimal.Operations.Count, Is.LessThan(sequence.Operations.Count), report);
            Assert.That(
                EvaluateMutant(minimal, "explicit-cleanup")?.Category,
                Is.EqualTo(mismatch.Category),
                report
            );
            Assert.That(Evaluate(minimal, false), Is.Null, report);
        }

        [Test]
        public void ExplicitNestedHandlesAlternateAllKindsThroughDepthTen(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            foreach (int offset in new[] { 0, 1, 2 })
            foreach (int depth in new[] { 1, 10 })
            {
                BusTraceSequence sequence = new(
                    scenario,
                    509,
                    new[]
                    {
                        new BusTraceOperation(BusTraceOperationKind.Register, handleSlot: 0),
                        new BusTraceOperation(
                            BusTraceOperationKind.Register,
                            token: 1,
                            context: 1,
                            kindOffset: offset,
                            handleSlot: 1
                        ),
                        new BusTraceOperation(
                            BusTraceOperationKind.CopyHandle,
                            token: 1,
                            handleSlot: 2,
                            sourceHandleSlot: 1
                        ),
                        new BusTraceOperation(
                            BusTraceOperationKind.EmitNested,
                            value: 11,
                            nestedToken: 1,
                            depth: depth,
                            handleSlot: 0,
                            sourceHandleSlot: 2
                        ),
                        new BusTraceOperation(BusTraceOperationKind.Emit, value: 19),
                    }
                );
                IReadOnlyList<BusTraceObservation> observations = DifferentialBusTrace.Replay(
                    sequence,
                    item => CreateAdapter(item, false)
                );
                string report =
                    $"[{scenario.Kind}] offset={offset}, depth={depth}: "
                    + string.Join("\n", observations);
                Assert.That(observations.All(item => item.Exception == null), Is.True, report);
                // A sibling on the same kind must not consume the selected trigger.
                Assert.That(
                    observations[3]
                        .Callbacks.Any(item => item.StartsWith($"nested-enter:depth={depth - 1},")),
                    Is.True,
                    report
                );
                Assert.That(
                    observations[3]
                        .Callbacks.Any(item => item.StartsWith($"nested-enter:depth={depth},")),
                    Is.False,
                    report
                );
                Assert.That(
                    observations[3].Callbacks.Count(item => item.StartsWith("nested-enter:")),
                    Is.EqualTo(depth),
                    report
                );
                Assert.That(
                    observations[3].Callbacks.Count(item => item.StartsWith("nested-return:")),
                    Is.EqualTo(depth),
                    report
                );
                Assert.That(
                    observations[4].Callbacks.All(item => !item.StartsWith("nested-")),
                    Is.True,
                    report
                );
                Assert.That(Evaluate(sequence, false), Is.Null, report);
            }
        }

        [Test]
        public void ExplicitCallbackValidityRequiresOwnedLiveTriggerAndNestedDependencies()
        {
            MessageScenario scenario = MessageScenario.Untargeted();
            foreach (BusTraceOperationKind kind in Enum.GetValues(typeof(BusTraceOperationKind)))
            {
                if (!DifferentialBusTrace.IsCallbackAction(kind))
                {
                    continue;
                }
                BusTraceOperation register = new(
                    BusTraceOperationKind.Register,
                    token: 3,
                    handleSlot: 0
                );
                BusTraceOperation nested = new(
                    BusTraceOperationKind.Register,
                    token: 2,
                    handleSlot: 1
                );
                BusTraceOperation callback = new(
                    kind,
                    token: 3,
                    handleSlot: 0,
                    nestedToken: kind == BusTraceOperationKind.EmitNested ? 2 : 0,
                    depth: kind == BusTraceOperationKind.EmitNested ? 1 : 0,
                    sourceHandleSlot: kind == BusTraceOperationKind.EmitNested ? 1 : -1
                );
                BusTraceOperation[] valid = { register, nested, callback };
                string report = string.Join(";", valid);
                Assert.That(
                    DifferentialBusTrace.IsValid(new(scenario, 509, valid)),
                    Is.True,
                    report
                );
                Assert.That(
                    DifferentialBusTrace.IsValid(new(scenario, 509, valid, 8)),
                    Is.False,
                    report
                );
                foreach (
                    BusTraceOperation[] invalid in new[]
                    {
                        new[] { callback },
                        new[]
                        {
                            new BusTraceOperation(BusTraceOperationKind.Register, handleSlot: 0),
                            nested,
                            callback,
                        },
                        new[]
                        {
                            register,
                            nested,
                            new BusTraceOperation(
                                BusTraceOperationKind.Remove,
                                token: 3,
                                handleSlot: 0
                            ),
                            callback,
                        },
                        new[]
                        {
                            register,
                            nested,
                            new BusTraceOperation(
                                kind,
                                token: 3,
                                handleSlot: 0,
                                sourceHandleSlot: 1
                            ),
                        },
                    }
                )
                {
                    Assert.That(
                        DifferentialBusTrace.IsValid(new(scenario, 509, invalid)),
                        Is.False,
                        string.Join(";", invalid)
                    );
                }
                if (kind == BusTraceOperationKind.EmitNested)
                {
                    Assert.That(
                        DifferentialBusTrace.IsValid(
                            new(scenario, 509, new[] { register, callback })
                        ),
                        Is.False,
                        report
                    );
                    Assert.That(
                        DifferentialBusTrace.IsValid(
                            new(
                                scenario,
                                509,
                                new[]
                                {
                                    register,
                                    new BusTraceOperation(
                                        BusTraceOperationKind.Register,
                                        token: 1,
                                        handleSlot: 1
                                    ),
                                    callback,
                                }
                            )
                        ),
                        Is.False,
                        report
                    );
                }
            }
        }

        [Test]
        public void VersionNineGeneratesEveryExplicitCallbackActionDeterministically()
        {
            HashSet<BusTraceOperationKind> callbacks = new();
            HashSet<BusTraceOperationKind> all = new();
            for (uint seed = 0; seed < 32; ++seed)
            {
                BusTraceSequence sequence = DifferentialBusTrace.Generate(
                    MessageScenario.Untargeted(),
                    seed,
                    256,
                    9
                );
                Assert.That(DifferentialBusTrace.IsValid(sequence), Is.True, $"seed={seed}");
                CollectionAssert.AreEqual(
                    sequence.Operations,
                    DifferentialBusTrace.Generate(sequence.Scenario, seed, 256, 9).Operations,
                    $"seed={seed}"
                );
                foreach (BusTraceOperation operation in sequence.Operations)
                {
                    all.Add(operation.Kind);
                    if (
                        operation.HandleSlot >= 0
                        && DifferentialBusTrace.IsCallbackAction(operation.Kind)
                    )
                    {
                        callbacks.Add(operation.Kind);
                    }
                }
            }
            CollectionAssert.AreEquivalent(
                Enum.GetValues(typeof(BusTraceOperationKind))
                    .Cast<BusTraceOperationKind>()
                    .Where(kind => !DifferentialBusTrace.IsGlobalOverride(kind)),
                all,
                "Version nine retains the full operation vocabulary."
            );
            CollectionAssert.AreEquivalent(
                new[]
                {
                    BusTraceOperationKind.EmitWithReset,
                    BusTraceOperationKind.EmitWithThrow,
                    BusTraceOperationKind.EmitWithDisable,
                    BusTraceOperationKind.EmitWithHandlerActive,
                    BusTraceOperationKind.EmitNested,
                },
                callbacks,
                "Every callback action must use explicit handles in generated sequences."
            );
        }

        [Test]
        public void HandlerActivityReplayKeepsTokenEnabledAndAffectsCurrentDispatch(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceOperation[] operations =
            {
                new(BusTraceOperationKind.Register, priority: -1),
                new(BusTraceOperationKind.Register, token: 1, priority: 1),
                new(BusTraceOperationKind.SetHandlerActive, token: 1),
                new(BusTraceOperationKind.Emit, value: 17),
                new(BusTraceOperationKind.Enable, token: 1),
                new(BusTraceOperationKind.Emit, value: 19),
                new(
                    BusTraceOperationKind.EmitWithHandlerActive,
                    value: 23,
                    handlerToken: 1,
                    handlerActive: true
                ),
                new(BusTraceOperationKind.EmitWithHandlerActive, value: 29, handlerToken: 1),
                new(BusTraceOperationKind.Disable, token: 1),
                new(BusTraceOperationKind.SetHandlerActive, token: 1, handlerActive: true),
                new(BusTraceOperationKind.Emit, value: 31),
                new(BusTraceOperationKind.Enable, token: 1),
                new(BusTraceOperationKind.Emit, value: 37),
            };
            BusTraceSequence sequence = new(scenario, 509, operations);
            IReadOnlyList<BusTraceObservation> observations = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            Assert.That(
                observations.All(observation => observation.Exception == null),
                Is.True,
                $"[{scenario.Kind}] {string.Join(";", observations)}"
            );
            Assert.That(
                Evaluate(sequence, false),
                Is.Null,
                $"[{scenario.Kind}] production replays must agree."
            );
            string report = $"[{scenario.Kind}] " + string.Join("\n", observations);
            StringAssert.Contains("enabled=1111", observations[2].State, report);
            StringAssert.Contains("handlerActive=1011", observations[2].State, report);
            CollectionAssert.AreEqual(
                new[] { "token=0,value=17" },
                observations[3].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=19" },
                observations[5].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=23", "token=1,value=23" },
                observations[6].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=29" },
                observations[7].Callbacks,
                report
            );
            StringAssert.Contains("enabled=1011", observations[9].State, report);
            StringAssert.Contains("handlerActive=1111", observations[9].State, report);
            CollectionAssert.AreEqual(
                new[] { "token=0,value=31" },
                observations[10].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=37", "token=1,value=37" },
                observations[12].Callbacks,
                report
            );
        }

        [Test]
        public void IgnoredHandlerActivityIsDetectedAndShrunk(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool fromCallback
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Enable, token: 3),
                    new BusTraceOperation(BusTraceOperationKind.Register, priority: -1),
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 1, priority: 1),
                    fromCallback
                        ? new BusTraceOperation(
                            BusTraceOperationKind.EmitWithHandlerActive,
                            value: 17,
                            handlerToken: 1
                        )
                        : new BusTraceOperation(BusTraceOperationKind.SetHandlerActive, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 19),
                }
            );
            BusTraceMismatch mismatch = EvaluateMutant(sequence, "handler-active");
            string report =
                $"[{scenario.Kind}] fromCallback={fromCallback}: "
                + mismatch?.BuildReport(sequence);
            Assert.That(mismatch, Is.Not.Null, report);
            Assert.That(
                mismatch.Category,
                Is.EqualTo(fromCallback ? "callbacks" : "state"),
                report
            );
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                sequence,
                replay => EvaluateMutant(replay, "handler-active")
            );
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(
                EvaluateMutant(minimal, "handler-active")?.Category,
                Is.EqualTo(mismatch.Category),
                report
            );
            Assert.That(minimal.Seed, Is.EqualTo(sequence.Seed), report);
            Assert.That(minimal.Version, Is.EqualTo(sequence.Version), report);
            CollectionAssert.AreEqual(
                fromCallback
                    ? new[]
                    {
                        BusTraceOperationKind.Register,
                        BusTraceOperationKind.Register,
                        BusTraceOperationKind.EmitWithHandlerActive,
                    }
                    : new[] { BusTraceOperationKind.SetHandlerActive },
                minimal.Operations.Select(operation => operation.Kind),
                report
            );
        }

        [Test]
        public void SkippedHandlerActivityCallbackDoesNotArmLaterEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register, priority: -1),
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 1, priority: 1),
                    new BusTraceOperation(BusTraceOperationKind.SetHandlerActive),
                    new BusTraceOperation(
                        BusTraceOperationKind.EmitWithHandlerActive,
                        value: 17,
                        handlerToken: 1
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.SetHandlerActive,
                        handlerActive: true
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 19),
                }
            );
            IReadOnlyList<BusTraceObservation> observations = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            string report = $"[{scenario.Kind}] " + string.Join(";", observations);
            Assert.That(
                observations.All(observation => observation.Exception == null),
                Is.True,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=1,value=17" },
                observations[3].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=19", "token=1,value=19" },
                observations[5].Callbacks,
                report
            );
            StringAssert.Contains("handlerActive=1111", observations[5].State, report);
        }

        [Test]
        public void HandlerActivityValidityPreservesTriggerDependenciesAndVersion()
        {
            MessageScenario scenario = MessageScenario.Untargeted();
            BusTraceOperation register = new(BusTraceOperationKind.Register);
            BusTraceOperation toggle = new(BusTraceOperationKind.SetHandlerActive, token: 1);
            BusTraceOperation callback = new(
                BusTraceOperationKind.EmitWithHandlerActive,
                handlerToken: 1
            );
            foreach (
                BusTraceOperation[] operations in new[]
                {
                    new[] { toggle },
                    new[] { register, callback },
                    new[]
                    {
                        register,
                        new BusTraceOperation(BusTraceOperationKind.Disable),
                        callback,
                    },
                    new[]
                    {
                        register,
                        new BusTraceOperation(BusTraceOperationKind.SetHandlerActive),
                        callback,
                    },
                }
            )
            {
                string report = string.Join(";", operations);
                Assert.That(
                    DifferentialBusTrace.IsValid(new BusTraceSequence(scenario, 509, operations)),
                    Is.True,
                    report
                );
                Assert.That(
                    DifferentialBusTrace.IsValid(
                        new BusTraceSequence(scenario, 509, operations, 6)
                    ),
                    Is.False,
                    report
                );
            }
            foreach (
                BusTraceOperation[] operations in new[]
                {
                    new[] { callback },
                    new[]
                    {
                        register,
                        new BusTraceOperation(BusTraceOperationKind.Remove),
                        callback,
                    },
                    new[]
                    {
                        register,
                        new BusTraceOperation(
                            BusTraceOperationKind.EmitWithHandlerActive,
                            handlerToken: -1
                        ),
                    },
                    new[]
                    {
                        register,
                        new BusTraceOperation(
                            BusTraceOperationKind.EmitWithHandlerActive,
                            handlerToken: BusTraceSequence.TokenCount
                        ),
                    },
                    new[]
                    {
                        new BusTraceOperation(
                            BusTraceOperationKind.SetHandlerActive,
                            handlerToken: 1
                        ),
                    },
                    new[]
                    {
                        new BusTraceOperation(BusTraceOperationKind.Emit, handlerActive: true),
                    },
                }
            )
            {
                Assert.That(
                    DifferentialBusTrace.IsValid(new BusTraceSequence(scenario, 509, operations)),
                    Is.False,
                    string.Join(";", operations)
                );
            }
            BusTraceSequence generated = DifferentialBusTrace.Generate(scenario, 17, 256, 7);
            Assert.That(
                DifferentialBusTrace.IsValid(generated),
                Is.True,
                "Version 7 seed 17 must preserve callback dependencies."
            );
            foreach (
                BusTraceOperationKind kind in new[]
                {
                    BusTraceOperationKind.SetHandlerActive,
                    BusTraceOperationKind.EmitWithHandlerActive,
                }
            )
            {
                foreach (bool active in new[] { false, true })
                {
                    Assert.That(
                        generated.Operations.Any(operation =>
                            operation.Kind == kind && operation.HandlerActive == active
                        ),
                        Is.True,
                        $"Version 7 seed 17 must generate {kind} active={active}."
                    );
                }
            }
            StringAssert.Contains(
                "handlerToken=1,handlerActive=False",
                callback.ToString(),
                "Replay reports must preserve handler identity and requested activity."
            );
        }

        [Test]
        public void NestedReplayRestoresEmissionScopeAndRemainsUsable(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(0, 1, 2)] int innerKindOffset,
            [Values(1, 10)] int depth
        )
        {
            BusTraceSequence sequence = NestedSequence(scenario, innerKindOffset, depth);
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            string report = DescribeReplay(sequence, control, candidate);
            Assert.That(DifferentialBusTrace.Compare(control, candidate), Is.Null, report);
            Assert.That(control.All(item => item.Exception == null), Is.True, report);
            BusTraceObservation nested = control[innerKindOffset == 0 ? 2 : 3];
            CollectionAssert.AreEqual(
                NestedCallbacks(innerKindOffset, depth, 1),
                nested.Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=31" },
                control[control.Count - 1].Callbacks,
                report
            );
        }

        [Test]
        public void NestedCallbackFailureUnwindsScopeAndDoesNotRearmLaterEmissions(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(0, 1, 2)] int innerKindOffset,
            [Values(1, 10)] int depth
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionTicks: 0,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            using ThrowOnceInNestedCallbackAdapter adapter = new(scenario, bus, depth + 1);
            BusTraceSequence sequence = NestedSequence(scenario, innerKindOffset, depth);
            int nestedIndex = innerKindOffset == 0 ? 2 : 3;
            string label = $"[{scenario.Kind}] innerKindOffset={innerKindOffset}, depth={depth}";
            for (int index = 0; index < nestedIndex; ++index)
            {
                BusTraceObservation setup = adapter.Execute(sequence.Operations[index]);
                Assert.That(setup.Exception, Is.Null, label + ": " + setup);
            }
            BusTraceOperation nestedOperation = sequence.Operations[nestedIndex];
            BusTraceObservation failed = adapter.Execute(nestedOperation);
            string report = label + ": " + failed;
            Assert.That(
                failed.Exception,
                Is.EqualTo(
                    "System.InvalidOperationException: intentional nested trace callback failure"
                ),
                report
            );
            CollectionAssert.AreEqual(
                NestedCallbacks(innerKindOffset, depth, 1),
                failed.Callbacks,
                report
            );
            Assert.That(bus.IsDispatching, Is.False, report);
            Assert.That(bus.EmissionId, Is.EqualTo(depth + 1), report);

            BusTraceObservation later = adapter.Execute(sequence.Operations[nestedIndex + 1]);
            report = label + ": after failure: " + later;
            Assert.That(later.Exception, Is.Null, report);
            CollectionAssert.AreEqual(new[] { "token=0,value=31" }, later.Callbacks, report);
            Assert.That(bus.IsDispatching, Is.False, report);
            Assert.That(bus.EmissionId, Is.EqualTo(depth + 2), report);

            BusTraceObservation recovered = adapter.Execute(nestedOperation);
            report = label + ": recovered nested emit: " + recovered;
            Assert.That(recovered.Exception, Is.Null, report);
            CollectionAssert.AreEqual(
                NestedCallbacks(innerKindOffset, depth, depth + 3),
                recovered.Callbacks,
                report
            );
            Assert.That(bus.IsDispatching, Is.False, report);
            Assert.That(bus.EmissionId, Is.EqualTo(2 * depth + 3), report);
        }

        private static List<string> NestedCallbacks(
            int innerKindOffset,
            int depth,
            int firstEmission
        )
        {
            List<string> expected = new();
            for (int level = 0; level <= depth; ++level)
            {
                int token = innerKindOffset == 0 ? 0 : level % 2;
                expected.Add($"token={token},value={17 + level}");
                if (level < depth)
                {
                    expected.Add($"nested-enter:depth={level},emission={firstEmission + level}");
                }
            }
            for (int level = depth - 1; level >= 0; --level)
            {
                expected.Add($"nested-return:depth={level},emission={firstEmission + level}");
            }
            return expected;
        }

        [Test]
        public void NestedAndCallbackDisableMutantsAreDetectedAndShrunk(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values("nested", "callback-disable")] string fault
        )
        {
            BusTraceSequence original =
                fault == "nested"
                    ? NestedSequence(scenario, 1, 10)
                    : new BusTraceSequence(
                        scenario,
                        509,
                        new[]
                        {
                            new BusTraceOperation(BusTraceOperationKind.Enable, token: 3),
                            new BusTraceOperation(BusTraceOperationKind.Register),
                            new BusTraceOperation(BusTraceOperationKind.EmitWithDisable, value: 17),
                            new BusTraceOperation(BusTraceOperationKind.Emit, value: 19),
                            new BusTraceOperation(BusTraceOperationKind.Enable),
                            new BusTraceOperation(BusTraceOperationKind.Emit, value: 31),
                        }
                    );
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                original,
                kind => CreateAdapter(kind, false)
            );
            Assert.That(control.All(item => item.Exception == null), Is.True);
            if (fault == "callback-disable")
            {
                CollectionAssert.AreEqual(new[] { "token=0,value=17" }, control[2].Callbacks);
                Assert.That(control[3].Callbacks, Is.Empty);
                CollectionAssert.AreEqual(new[] { "token=0,value=31" }, control[5].Callbacks);
            }
            BusTraceMismatch mismatch = EvaluateMutant(original, fault);
            Assert.That(
                mismatch,
                Is.Not.Null,
                $"[{scenario.Kind}] {fault} must alter real operations."
            );
            string report = mismatch.BuildReport(original);
            Assert.That(
                mismatch.Category,
                Is.EqualTo(fault == "nested" ? "callbacks" : "state"),
                report
            );
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                original,
                sequence => EvaluateMutant(sequence, fault)
            );
            BusTraceMismatch replayed = EvaluateMutant(minimal, fault);
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(replayed?.Category, Is.EqualTo(mismatch.Category), report);
            Assert.That(minimal.Seed, Is.EqualTo(original.Seed), report);
            Assert.That(minimal.Version, Is.EqualTo(BusTraceSequence.GeneratorVersion), report);
            CollectionAssert.AreEqual(
                fault == "nested"
                    ? new[]
                    {
                        BusTraceOperationKind.Register,
                        BusTraceOperationKind.Register,
                        BusTraceOperationKind.EmitNested,
                    }
                    : new[]
                    {
                        BusTraceOperationKind.Register,
                        BusTraceOperationKind.EmitWithDisable,
                    },
                minimal.Operations.Select(item => item.Kind),
                report
            );
        }

        [Test]
        public void NestedReplayRequiresBothHandlesAndBoundedDepth()
        {
            MessageScenario scenario = MessageScenario.Untargeted();
            BusTraceOperation first = new(BusTraceOperationKind.Register);
            BusTraceOperation second = new(BusTraceOperationKind.Register, token: 1, kindOffset: 1);
            BusTraceOperation nested = new(
                BusTraceOperationKind.EmitNested,
                nestedToken: 1,
                depth: 10
            );
            Assert.That(
                DifferentialBusTrace.IsValid(
                    new BusTraceSequence(scenario, 509, new[] { first, second, nested })
                ),
                Is.True
            );
            foreach (
                BusTraceOperation[] operations in new[]
                {
                    new[] { first, nested },
                    new[] { second, nested },
                    new[]
                    {
                        first,
                        second,
                        new BusTraceOperation(BusTraceOperationKind.Remove, token: 1),
                        nested,
                    },
                    new[] { first, new BusTraceOperation(BusTraceOperationKind.EmitNested) },
                    new[]
                    {
                        first,
                        new BusTraceOperation(BusTraceOperationKind.EmitNested, depth: 11),
                    },
                    new[] { first, new BusTraceOperation(BusTraceOperationKind.Emit, depth: 1) },
                    new[] { new BusTraceOperation(BusTraceOperationKind.Register, kindOffset: 3) },
                }
            )
            {
                Assert.That(
                    DifferentialBusTrace.IsValid(new BusTraceSequence(scenario, 509, operations)),
                    Is.False,
                    string.Join(";", operations)
                );
            }
            Assert.That(
                DifferentialBusTrace.IsValid(
                    new BusTraceSequence(
                        scenario,
                        509,
                        new[] { first, second, nested },
                        generatorVersion: 4
                    )
                ),
                Is.False
            );
            BusTraceSequence generated = DifferentialBusTrace.Generate(scenario, 17, 256);
            Assert.That(DifferentialBusTrace.IsValid(generated), Is.True);
            foreach (
                BusTraceOperationKind kind in new[]
                {
                    BusTraceOperationKind.EmitNested,
                    BusTraceOperationKind.EmitWithDisable,
                }
            )
            {
                Assert.That(
                    generated.Operations.Any(operation => operation.Kind == kind),
                    Is.True,
                    kind.ToString()
                );
            }
        }

        private static BusTraceSequence NestedSequence(
            MessageScenario scenario,
            int offset,
            int depth
        )
        {
            List<BusTraceOperation> operations = new()
            {
                new(BusTraceOperationKind.Enable, token: 3),
                new(BusTraceOperationKind.Register),
            };
            if (offset != 0)
            {
                operations.Add(
                    new BusTraceOperation(
                        BusTraceOperationKind.Register,
                        token: 1,
                        context: 1,
                        kindOffset: offset
                    )
                );
            }
            operations.Add(
                new BusTraceOperation(
                    BusTraceOperationKind.EmitNested,
                    value: 17,
                    nestedToken: offset == 0 ? 0 : 1,
                    depth: depth
                )
            );
            operations.Add(new BusTraceOperation(BusTraceOperationKind.Emit, value: 31));
            return new BusTraceSequence(scenario, 509, operations);
        }

        [Test]
        public void TrimRecoversStorageAndPreservesReuseAfterStaleRemovalAndReset(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool force
        )
        {
            List<BusTraceOperation> operations = new()
            {
                new(BusTraceOperationKind.Register),
                new(BusTraceOperationKind.Emit, value: 10),
                new(BusTraceOperationKind.Remove),
                new(BusTraceOperationKind.Trim),
            };
            if (!force)
            {
                // The real empty emit advances the bus tick without touching empty sinks.
                // No clock or process-global settings are changed by this replay.
                operations.Add(new BusTraceOperation(BusTraceOperationKind.Emit, value: 11));
            }
            int reclaimed = operations.Count;
            operations.AddRange(
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: force ? 1 : 0),
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: force ? 1 : 0),
                    new BusTraceOperation(BusTraceOperationKind.Register, context: 1),
                    new BusTraceOperation(BusTraceOperationKind.RemoveStale),
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: force ? 1 : 0),
                    new BusTraceOperation(BusTraceOperationKind.Emit, context: 1, value: 12),
                    new BusTraceOperation(
                        BusTraceOperationKind.EmitWithReset,
                        context: 1,
                        value: 13
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: force ? 1 : 0),
                    new BusTraceOperation(BusTraceOperationKind.Disable),
                    new BusTraceOperation(BusTraceOperationKind.Enable),
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: force ? 1 : 0),
                    new BusTraceOperation(BusTraceOperationKind.Emit, context: 1, value: 14),
                    new BusTraceOperation(BusTraceOperationKind.Remove),
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: 1),
                }
            );
            BusTraceSequence sequence = new(scenario, 509, operations);
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> candidate = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            string report = $"force={force}\n" + DescribeReplay(sequence, control, candidate);
            Assert.That(DifferentialBusTrace.Compare(control, candidate), Is.Null, report);
            Assert.That(control.All(item => item.Exception == null), Is.True, report);
            Assert.That(control[2].OccupiedTypeSlots, Is.GreaterThan(0), report);
            Assert.That(control[3].TrimResult.Value.TypeSlotsEvicted, Is.Zero, report);
            Assert.That(control[3].TrimResult.Value.TargetSlotsEvicted, Is.Zero, report);
            Assert.That(
                control[3].OccupiedTypeSlots,
                Is.EqualTo(control[2].OccupiedTypeSlots),
                report
            );
            Assert.That(
                control[3].OccupiedTargetSlots,
                Is.EqualTo(control[2].OccupiedTargetSlots),
                report
            );
            Assert.That(
                control[reclaimed].TrimResult.Value.TypeSlotsEvicted,
                Is.GreaterThan(0),
                report
            );
            Assert.That(
                control[reclaimed].TrimResult.Value.TargetSlotsEvicted,
                scenario.Kind == MessageKind.Untargeted ? Is.Zero : Is.GreaterThan(0),
                report
            );
            Assert.That(control[reclaimed + 1].TrimResult.Value.TypeSlotsEvicted, Is.Zero, report);
            Assert.That(
                control[reclaimed + 1].TrimResult.Value.TargetSlotsEvicted,
                Is.Zero,
                report
            );
            foreach (
                int index in new[] { reclaimed, reclaimed + 1, reclaimed + 7, control.Count - 1 }
            )
            {
                Assert.That(control[index].OccupiedTypeSlots, Is.Zero, report);
                Assert.That(control[index].OccupiedTargetSlots, Is.Zero, report);
            }
            foreach (int index in new[] { reclaimed + 4, reclaimed + 10 })
            {
                Assert.That(control[index].OccupiedTypeSlots, Is.GreaterThan(0), report);
                Assert.That(control[index].TrimResult.Value.TypeSlotsEvicted, Is.Zero, report);
                Assert.That(control[index].TrimResult.Value.TargetSlotsEvicted, Is.Zero, report);
            }
            CollectionAssert.AreEqual(
                new[] { "token=0,value=12" },
                control[reclaimed + 5].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=13" },
                control[reclaimed + 6].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=14" },
                control[reclaimed + 11].Callbacks,
                report
            );
            for (int index = 0; index < control.Count; ++index)
            {
                bool isTrim = operations[index].Kind == BusTraceOperationKind.Trim;
                Assert.That(control[index].TrimResult.HasValue, Is.EqualTo(isTrim), report);
                if (isTrim)
                {
                    Assert.That(
                        control[index].TrimResult.Value.LiveTypeSlotsRemaining,
                        Is.EqualTo(control[index].OccupiedTypeSlots),
                        report
                    );
                }
            }
            if (!force)
            {
                Assert.That(control[4].Callbacks, Is.Empty, report);
            }
        }

        [Test]
        public void IgnoredForceTrimIsDetectedAndShrunkUsingActualReclamation(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence original = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Enable, token: 3),
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 9),
                    new BusTraceOperation(BusTraceOperationKind.Remove),
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: 1),
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 10),
                }
            );
            BusTraceMismatch mismatch = EvaluateMutant(original, "trim-force");
            Assert.That(mismatch, Is.Not.Null, "Ignoring force must change actual trim output.");
            string report = mismatch.BuildReport(original);
            Assert.That(mismatch.Category, Is.EqualTo("trim"), report);
            Assert.That(mismatch.Control.Exception, Is.Null, report);
            Assert.That(mismatch.Candidate.Exception, Is.Null, report);
            Assert.That(mismatch.Control.OccupiedTypeSlots, Is.Zero, report);
            Assert.That(mismatch.Candidate.OccupiedTypeSlots, Is.GreaterThan(0), report);
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                original,
                sequence => EvaluateMutant(sequence, "trim-force")
            );
            CollectionAssert.AreEqual(
                new[]
                {
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.Remove,
                    BusTraceOperationKind.Trim,
                },
                minimal.Operations.Select(operation => operation.Kind),
                report
            );
            Assert.That(minimal.Version, Is.EqualTo(BusTraceSequence.GeneratorVersion), report);
            Assert.That(minimal.Seed, Is.EqualTo(original.Seed), report);
            Assert.That(
                EvaluateMutant(minimal, "trim-force")?.Category,
                Is.EqualTo("trim"),
                report
            );
            Assert.That(Evaluate(minimal, dropEmits: false), Is.Null, report);
        }

        [Test]
        public void TrimComparisonIncludesResultPresenceEveryResultFieldAndBothSlotCounts(
            [Values(
                "presence",
                "types",
                "targets",
                "pools",
                "live",
                "occupied-types",
                "occupied-targets"
            )]
                string difference
        )
        {
            IMessageBus.TrimResult baseline = new(1, 2, 3, 4);
            IMessageBus.TrimResult? changed = difference switch
            {
                "presence" => null,
                "types" => new IMessageBus.TrimResult(9, 2, 3, 4),
                "targets" => new IMessageBus.TrimResult(1, 9, 3, 4),
                "pools" => new IMessageBus.TrimResult(1, 2, 9, 4),
                "live" => new IMessageBus.TrimResult(1, 2, 3, 9),
                _ => baseline,
            };
            // Synthetic values test only the comparator; replay adapters always observe the real bus.
            BusTraceObservation control = new(Array.Empty<string>(), "same", null, baseline, 4, 2);
            BusTraceObservation candidate = new(
                Array.Empty<string>(),
                "same",
                null,
                changed,
                difference == "occupied-types" ? 9 : 4,
                difference == "occupied-targets" ? 9 : 2
            );
            BusTraceMismatch mismatch = DifferentialBusTrace.Compare(
                new[] { control },
                new[] { candidate }
            );
            Assert.That(mismatch, Is.Not.Null, difference);
            Assert.That(
                mismatch.Category,
                Is.EqualTo(
                    difference.StartsWith("occupied-", StringComparison.Ordinal)
                        ? "storage"
                        : "trim"
                ),
                difference
            );
            Assert.That(
                DifferentialBusTrace.Compare(new[] { control }, new[] { control }),
                Is.Null,
                difference
            );
        }

        [Test]
        public void VersionFourRequiresValidTrimFlagsAndGeneratesBothModes()
        {
            MessageScenario scenario = MessageScenario.Untargeted();
            foreach (int version in new[] { 1, 2, 3, 4 })
            {
                foreach (int flag in new[] { -1, 0, 1, 2 })
                {
                    BusTraceSequence sequence = new(
                        scenario,
                        509,
                        new[] { new BusTraceOperation(BusTraceOperationKind.Trim, value: flag) },
                        version
                    );
                    Assert.That(
                        DifferentialBusTrace.IsValid(sequence),
                        Is.EqualTo(version == 4 && (flag == 0 || flag == 1)),
                        $"version={version}, flag={flag}"
                    );
                }
            }
            BusTraceSequence generated = DifferentialBusTrace.Generate(scenario, 17, 256, 4);
            Assert.That(
                DifferentialBusTrace.IsValid(generated),
                Is.True,
                "Generated trim dependencies must be valid."
            );
            foreach (int flag in new[] { 0, 1 })
            {
                Assert.That(
                    generated.Operations.Any(operation =>
                        operation.Kind == BusTraceOperationKind.Trim && operation.Value == flag
                    ),
                    Is.True,
                    $"Generator v4 must exercise Trim(force={flag})."
                );
            }
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
        public void GeneratedReplaysMatchWithOnlyRequestedCallbackFailures(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(17, 42)] int seed,
            [Values(3, 4, 5, 6, 7, 8, 9, 10)] int version
        )
        {
            BusTraceSequence sequence = DifferentialBusTrace.Generate(
                scenario,
                (uint)seed,
                32,
                version
            );
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
                    ?? $"[{scenario.Kind}] seed={seed}: version {version} replays must match."
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
            [Values("reset", "throw", "nested", "disable", "handler")] string callback
        )
        {
            foreach (int handleSlot in new[] { -1, 0 })
            foreach (string suppression in new[] { "disabled", "inactive", "route" })
            {
                if (suppression == "route" && scenario.Kind == MessageKind.Untargeted)
                {
                    continue;
                }
                using MessageBusTraceAdapter adapter = CreateAdapter(scenario, false);
                adapter.Execute(
                    new BusTraceOperation(BusTraceOperationKind.Register, handleSlot: handleSlot)
                );
                adapter.Execute(
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 1, priority: 1)
                );
                if (suppression == "disabled")
                {
                    adapter.Execute(new BusTraceOperation(BusTraceOperationKind.Disable));
                }
                if (suppression == "inactive")
                {
                    adapter.Execute(new BusTraceOperation(BusTraceOperationKind.SetHandlerActive));
                }
                BusTraceObservation untriggered = adapter.Execute(
                    new BusTraceOperation(
                        callback switch
                        {
                            "throw" => BusTraceOperationKind.EmitWithThrow,
                            "nested" => BusTraceOperationKind.EmitNested,
                            "disable" => BusTraceOperationKind.EmitWithDisable,
                            "handler" => BusTraceOperationKind.EmitWithHandlerActive,
                            _ => BusTraceOperationKind.EmitWithReset,
                        },
                        context: suppression == "route" ? 1 : 0,
                        value: 10,
                        depth: callback == "nested" ? 10 : 0,
                        handlerToken: callback == "handler" ? 1 : 0,
                        handleSlot: handleSlot,
                        sourceHandleSlot: callback == "nested" ? handleSlot : -1
                    )
                );
                string label =
                    $"[{scenario.Kind}] callback={callback}, suppression={suppression}, handleSlot={handleSlot}: {untriggered}";
                Assert.That(untriggered.Exception, Is.Null, label);
                CollectionAssert.AreEqual(
                    suppression == "route" ? Array.Empty<string>() : new[] { "token=1,value=10" },
                    untriggered.Callbacks,
                    label
                );
                adapter.Execute(new BusTraceOperation(BusTraceOperationKind.Enable));
                adapter.Execute(
                    new BusTraceOperation(
                        BusTraceOperationKind.SetHandlerActive,
                        handlerActive: true
                    )
                );
                foreach (int value in new[] { 11, 12 })
                {
                    BusTraceObservation later = adapter.Execute(
                        new BusTraceOperation(BusTraceOperationKind.Emit, value: value)
                    );
                    Assert.That(later.Exception, Is.Null, label + "\n" + later);
                    CollectionAssert.AreEquivalent(
                        new[]
                        {
                            $"token=0,value={value}"
                                + (handleSlot >= 0 ? ",registration=0,callback=0" : string.Empty),
                            $"token=1,value={value}",
                        },
                        later.Callbacks,
                        label + "\n" + later
                    );
                }
            }
        }

        [Test]
        public void ResetGenerationWrapPreservesCopiedHandlesAndReplacementRegistrations(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(long.MaxValue, -1L)] long initialGeneration
        )
        {
            BusTraceSequence sequence = ResetGenerationBoundarySequence(scenario);
            List<long> generations = new();
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            IReadOnlyList<BusTraceObservation> boundary = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateResetGenerationAdapter(kind, initialGeneration, generations)
            );
            string report =
                $"initialResetGeneration={initialGeneration}; "
                + DescribeReplay(sequence, control, boundary);
            Assert.That(boundary.All(item => item.Exception == null), Is.True, report);
            Assert.That(DifferentialBusTrace.Compare(control, boundary), Is.Null, report);
            CollectionAssert.AreEqual(
                new[] { unchecked(initialGeneration + 1), unchecked(initialGeneration + 2) },
                generations,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=13,registration=0,callback=0" },
                boundary[5].Callbacks,
                report
            );
            foreach (int index in new[] { 6, 25 })
            {
                Assert.That(boundary[index].Callbacks, Is.Empty, report);
            }
            foreach (int index in new[] { 12, 14 })
            {
                int value = sequence.Operations[index].Value;
                CollectionAssert.AreEqual(
                    new[]
                    {
                        $"token=0,value={value},registration=0,callback=3",
                        $"token=1,value={value},registration=4,callback=2",
                    },
                    boundary[index].Callbacks,
                    report
                );
            }
            foreach (int index in new[] { 17, 21 })
            {
                CollectionAssert.AreEqual(
                    new[]
                    {
                        $"token=0,value={sequence.Operations[index].Value},registration=0,callback=3",
                    },
                    boundary[index].Callbacks,
                    report
                );
            }
            Assert.That(boundary[24].OccupiedTypeSlots, Is.Zero, report);
            Assert.That(boundary[24].OccupiedTargetSlots, Is.Zero, report);
        }

        [Test]
        public void RejectedResetGenerationWrapIsDetectedAndShrunk(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(long.MaxValue, -1L)] long initialGeneration
        )
        {
            BusTraceSequence sequence = ResetGenerationBoundarySequence(scenario);
            BusTraceMismatch EvaluateBoundary(BusTraceSequence replay) =>
                DifferentialBusTrace.Compare(
                    DifferentialBusTrace.Replay(
                        replay,
                        kind => CreateResetGenerationAdapter(kind, initialGeneration)
                    ),
                    DifferentialBusTrace.Replay(
                        replay,
                        kind =>
                            CreateResetGenerationAdapter(kind, initialGeneration, rejectWrap: true)
                    )
                );
            BusTraceMismatch mismatch = EvaluateBoundary(sequence);
            string report =
                $"initialResetGeneration={initialGeneration}; " + mismatch?.BuildReport(sequence);
            Assert.That(mismatch, Is.Not.Null, report);
            Assert.That(mismatch.Index, Is.EqualTo(5), report);
            Assert.That(mismatch.Category, Is.EqualTo("callbacks"), report);
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(sequence, EvaluateBoundary);
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(minimal.Version, Is.EqualTo(sequence.Version), report);
            Assert.That(minimal.Seed, Is.EqualTo(sequence.Seed), report);
            Assert.That(minimal.Operations.Count, Is.LessThan(sequence.Operations.Count), report);
            Assert.That(EvaluateBoundary(minimal)?.Category, Is.EqualTo("callbacks"), report);
            Assert.That(
                minimal.Operations.Any(operation =>
                    operation.Kind == BusTraceOperationKind.EmitWithReset
                ),
                Is.True,
                report
            );
            for (int index = 0; index < minimal.Operations.Count; ++index)
            {
                List<BusTraceOperation> remaining = new(minimal.Operations);
                remaining.RemoveAt(index);
                BusTraceSequence deletion = new(scenario, minimal.Seed, remaining, minimal.Version);
                Assert.That(
                    !DifferentialBusTrace.IsValid(deletion)
                        || EvaluateBoundary(deletion)?.Category != mismatch.Category,
                    Is.True,
                    $"{report}; deletion={index}"
                );
            }
        }

        private static BusTraceSequence ResetGenerationBoundarySequence(MessageScenario scenario) =>
            new(
                scenario,
                509,
                new BusTraceOperation[]
                {
                    new(BusTraceOperationKind.Register, priority: -1, handleSlot: 0),
                    new(
                        BusTraceOperationKind.DuplicateRegistration,
                        handleSlot: 1,
                        sourceHandleSlot: 0
                    ),
                    new(BusTraceOperationKind.CopyHandle, handleSlot: 2, sourceHandleSlot: 0),
                    new(BusTraceOperationKind.Register, token: 1, priority: 1, handleSlot: 3),
                    new(BusTraceOperationKind.Emit, value: 11),
                    new(BusTraceOperationKind.EmitWithReset, value: 13, handleSlot: 2),
                    new(BusTraceOperationKind.Emit, value: 17),
                    new(BusTraceOperationKind.Register, token: 1, priority: 1, handleSlot: 4),
                    new(BusTraceOperationKind.Remove, token: 1, handleSlot: 3),
                    new(BusTraceOperationKind.Remove, handleSlot: 2),
                    new(BusTraceOperationKind.Register, priority: -1, handleSlot: 0),
                    new(BusTraceOperationKind.Remove, handleSlot: 2),
                    new(BusTraceOperationKind.Emit, value: 19),
                    new(BusTraceOperationKind.Remove, handleSlot: 1),
                    new(BusTraceOperationKind.Emit, value: 23),
                    new(BusTraceOperationKind.Disable),
                    new(BusTraceOperationKind.Enable),
                    new(BusTraceOperationKind.EmitWithReset, value: 29, handleSlot: 0),
                    new(BusTraceOperationKind.Remove, token: 1, handleSlot: 4),
                    new(BusTraceOperationKind.Disable),
                    new(BusTraceOperationKind.Enable),
                    new(BusTraceOperationKind.Emit, value: 31),
                    new(BusTraceOperationKind.Remove, handleSlot: 0),
                    new(BusTraceOperationKind.Remove, token: 1, handleSlot: 3),
                    new(BusTraceOperationKind.Trim, value: 1),
                    new(BusTraceOperationKind.Emit, value: 37),
                }
            );

        private static MessageBusTraceAdapter CreateResetGenerationAdapter(
            MessageScenario scenario,
            long initialGeneration,
            List<long> generations = null,
            bool rejectWrap = false
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionTicks: 0,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            bus.DiagnosticsMode = false;
            // Fault injection seeds only this isolated bus before any registrations exist.
            // Actual ResetState performs each increment, teardown, and snapshot invalidation.
            FieldInfo field = typeof(MessageBus).GetField(
                "_resetGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                field,
                Is.Not.Null,
                "The reset boundary fixture must seed the production generation."
            );
            field.SetValue(bus, initialGeneration);
            return new MessageBusTraceAdapter(
                scenario,
                bus,
                reset: () =>
                {
                    // Mutation: a boundary guard wrongly rejects signed overflow or zero.
                    // It changes the operation, never the oracle's observations.
                    if (rejectWrap && MessageBus.GetResetGeneration(bus) == initialGeneration)
                    {
                        return;
                    }
                    bus.ResetState();
                    generations?.Add(MessageBus.GetResetGeneration(bus));
                }
            );
        }

        [Test]
        public void ThrowingResetActionIsObservedAndDoesNotPoisonLaterEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionTicks: 0,
                idleEvictionEnabled: false,
                trimApiEnabled: true
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
                    .Generate(scenario, 17, 256, generatorVersion: 2)
                    .Operations.Any(operation =>
                        operation.Kind == BusTraceOperationKind.EmitWithReset
                    ),
                Is.True,
                "Version 2 generation must exercise callback reset, not only hand-written fixtures."
            );
        }

        [TestCase(0)]
        [TestCase(11)]
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
            BusTraceSequence generated = DifferentialBusTrace.Generate(scenario, 17, 256, 3);
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

        /// <remarks>
        /// 2026-09-06: A reused registration can make a five-operation trace irreducible by
        /// single deletion. Check that contract instead of assuming a global three-operation minimum.
        /// </remarks>
        [Test]
        public void ForeignHandleReplayPreservesBothOwnersAndDetectsAliasing(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Enable, token: 3),
                    new BusTraceOperation(BusTraceOperationKind.Register, priority: -1),
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 1, priority: 1),
                    new BusTraceOperation(BusTraceOperationKind.RemoveForeign, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 11),
                    new BusTraceOperation(BusTraceOperationKind.Disable, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.RemoveForeign, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Enable, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 12),
                    new BusTraceOperation(BusTraceOperationKind.Remove),
                    new BusTraceOperation(BusTraceOperationKind.RemoveForeign, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 13),
                    new BusTraceOperation(BusTraceOperationKind.Register, priority: -1),
                    new BusTraceOperation(BusTraceOperationKind.RemoveForeign, handleToken: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 14),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 15),
                }
            );
            IReadOnlyList<BusTraceObservation> control = DifferentialBusTrace.Replay(
                sequence,
                kind => CreateAdapter(kind, false)
            );
            string report =
                $"[{scenario.Kind}] foreign handle trace: " + string.Join("\n", control);
            Assert.That(control.All(item => item.Exception == null), Is.True, report);
            CollectionAssert.AreEqual(
                new[] { "token=0,value=11", "token=1,value=11" },
                control[4].Callbacks,
                report
            );
            CollectionAssert.AreEqual(
                new[] { "token=0,value=12", "token=1,value=12" },
                control[8].Callbacks,
                report
            );
            CollectionAssert.AreEqual(new[] { "token=1,value=13" }, control[11].Callbacks, report);
            CollectionAssert.AreEqual(
                new[] { "token=0,value=14", "token=1,value=14" },
                control[14].Callbacks,
                report
            );
            CollectionAssert.AreEqual(new[] { "token=0,value=15" }, control[16].Callbacks, report);
            Assert.That(Evaluate(sequence, dropEmits: false), Is.Null, report);

            BusTraceMismatch mismatch = EvaluateMutant(sequence, "foreign");
            Assert.That(mismatch, Is.Not.Null, report);
            Assert.That(mismatch.Index, Is.EqualTo(3), mismatch.BuildReport(sequence));
            Assert.That(mismatch.Category, Is.EqualTo("state"), mismatch.BuildReport(sequence));
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                sequence,
                replay => EvaluateMutant(replay, "foreign")
            );
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(EvaluateMutant(minimal, "foreign")?.Category, Is.EqualTo("state"), report);
            Assert.That(minimal.Seed, Is.EqualTo(sequence.Seed), report);
            Assert.That(minimal.Version, Is.EqualTo(sequence.Version), report);
            string minimalReport =
                $"[{scenario.Kind}] minimalCount={minimal.Operations.Count}; minimal={string.Join(";", minimal.Operations)}";
            Assert.That(
                minimal.Operations.Count,
                Is.LessThan(sequence.Operations.Count),
                minimalReport
            );
            for (int index = 0; index < minimal.Operations.Count; ++index)
            {
                List<BusTraceOperation> remaining = new(minimal.Operations);
                remaining.RemoveAt(index);
                BusTraceSequence deletion = new(
                    minimal.Scenario,
                    minimal.Seed,
                    remaining,
                    minimal.Version
                );
                Assert.That(
                    !DifferentialBusTrace.IsValid(deletion)
                        || EvaluateMutant(deletion, "foreign")?.Category != mismatch.Category,
                    Is.True,
                    $"{minimalReport}; deleting operation {index} must invalidate dependencies or lose the original mismatch."
                );
            }

            BusTraceSequence simple = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Enable, token: 3),
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.RemoveForeign, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit),
                }
            );
            BusTraceSequence simpleMinimum = DifferentialBusTrace.Shrink(
                simple,
                replay => EvaluateMutant(replay, "foreign")
            );
            Assert.That(DifferentialBusTrace.IsValid(simpleMinimum), Is.True, report);
            Assert.That(
                EvaluateMutant(simpleMinimum, "foreign")?.Category,
                Is.EqualTo("state"),
                report
            );
            Assert.That(simpleMinimum.Seed, Is.EqualTo(simple.Seed), report);
            Assert.That(simpleMinimum.Version, Is.EqualTo(simple.Version), report);
            CollectionAssert.AreEqual(
                new[]
                {
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.Register,
                    BusTraceOperationKind.RemoveForeign,
                },
                simpleMinimum.Operations.Select(operation => operation.Kind),
                $"[{scenario.Kind}] simple minimum must retain both owners and foreign cleanup: {string.Join(";", simpleMinimum.Operations)}"
            );
        }

        [Test]
        public void ForeignHandleValidityPreservesIssuedHandleDependencies()
        {
            MessageScenario scenario = MessageScenario.Untargeted();
            BusTraceOperation owner = new(BusTraceOperationKind.Register, token: 1);
            BusTraceOperation foreign = new(BusTraceOperationKind.RemoveForeign, handleToken: 1);
            foreach (
                BusTraceOperation[] operations in new[]
                {
                    new[] { owner, foreign, foreign },
                    new[]
                    {
                        owner,
                        new BusTraceOperation(BusTraceOperationKind.Remove, token: 1),
                        foreign,
                    },
                }
            )
            {
                Assert.That(
                    DifferentialBusTrace.IsValid(new BusTraceSequence(scenario, 509, operations)),
                    Is.True,
                    string.Join(";", operations)
                );
                Assert.That(
                    DifferentialBusTrace.IsValid(
                        new BusTraceSequence(scenario, 509, operations, generatorVersion: 5)
                    ),
                    Is.False,
                    string.Join(";", operations)
                );
            }
            foreach (
                BusTraceOperation[] operations in new[]
                {
                    new[] { foreign },
                    new[]
                    {
                        owner,
                        new BusTraceOperation(
                            BusTraceOperationKind.RemoveForeign,
                            token: 1,
                            handleToken: 1
                        ),
                    },
                    new[]
                    {
                        owner,
                        new BusTraceOperation(BusTraceOperationKind.RemoveForeign, handleToken: -1),
                    },
                    new[]
                    {
                        owner,
                        new BusTraceOperation(
                            BusTraceOperationKind.RemoveForeign,
                            handleToken: BusTraceSequence.TokenCount
                        ),
                    },
                    new[]
                    {
                        owner,
                        new BusTraceOperation(BusTraceOperationKind.Emit, handleToken: 1),
                    },
                }
            )
            {
                Assert.That(
                    DifferentialBusTrace.IsValid(new BusTraceSequence(scenario, 509, operations)),
                    Is.False,
                    string.Join(";", operations)
                );
            }
            BusTraceSequence generated = DifferentialBusTrace.Generate(scenario, 17, 256, 6);
            Assert.That(
                DifferentialBusTrace.IsValid(generated),
                Is.True,
                "Version 6 seed 17 must retain foreign handle dependencies."
            );
            Assert.That(
                generated.Operations.Any(operation =>
                    operation.Kind == BusTraceOperationKind.RemoveForeign
                ),
                Is.True,
                "Version 6 seed 17 must generate foreign cleanup."
            );
            StringAssert.Contains(
                "handleToken=1",
                foreign.ToString(),
                "Replay evidence must identify the foreign handle owner."
            );
        }

        private sealed class ForeignHandleAliasAdapter : MessageBusTraceAdapter
        {
            internal ForeignHandleAliasAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void Remove(BusTraceOperation operation)
            {
                if (operation.Kind == BusTraceOperationKind.RemoveForeign)
                {
                    // Simulate treating a foreign identity as the destination's own slot.
                    // The production API performs the removal; the observer stays unchanged.
                    Token(operation.Token).RemoveRegistration(Handle(operation.Token));
                    return;
                }
                base.Remove(operation);
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
                idleEvictionTicks: 0,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            bus.DiagnosticsMode = false;
            return fault switch
            {
                "explicit-callback" => new IgnoredExplicitCallbackAdapter(scenario, bus),
                "explicit-cleanup" => new WrongExplicitCallbackCleanupAdapter(scenario, bus),
                "order" => new EqualPriorityReorderAdapter(scenario, bus),
                "duplicate-as-copy" => new DuplicateAsCopyAdapter(scenario, bus),
                "reused-callback-order" => new ReusedCallbackOrderAdapter(scenario, bus),
                "wrong-independent-handle" => new WrongIndependentHandleAdapter(scenario, bus),
                "stale" => new StaleHandleReuseAdapter(scenario, bus),
                "foreign" => new ForeignHandleAliasAdapter(scenario, bus),
                "exception-cleanup" => new SkippedExceptionCleanupAdapter(scenario, bus),
                "diagnostics" => new DuplicateDiagnosticsAdapter(scenario, bus),
                "leak" => new RegistrationLeakAdapter(scenario, bus),
                "retention" => new RetainedDiagnosticReferenceAdapter(scenario, bus),
                "trim-force" => new IgnoredForceTrimAdapter(scenario, bus),
                "nested" => new MissingNestedEmitAdapter(scenario, bus),
                "callback-disable" => new IgnoredCallbackDisableAdapter(scenario, bus),
                "handler-active" => new IgnoredHandlerActivityAdapter(scenario, bus),
                _ => throw new ArgumentOutOfRangeException(nameof(fault)),
            };
        }

        // Each mutant changes a real operation before the shared observer reads production state.
        // None rewrites observations or implements message routing.
        private sealed class WrongExplicitCallbackCleanupAdapter : MessageBusTraceAdapter
        {
            internal WrongExplicitCallbackCleanupAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void CleanupThrowingCallback(BusTraceOperation operation) =>
                base.CleanupThrowingCallback(
                    new BusTraceOperation(
                        BusTraceOperationKind.Remove,
                        token: operation.Token,
                        handleSlot: operation.HandleSlot >= 0 ? 0 : -1
                    )
                );
        }

        private sealed class IgnoredExplicitCallbackAdapter : MessageBusTraceAdapter
        {
            internal IgnoredExplicitCallbackAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override bool MatchesCallback(
                BusTraceOperation operation,
                int token,
                int callbackIdentity,
                bool useNested = false
            ) =>
                operation.HandleSlot < 0
                && base.MatchesCallback(operation, token, callbackIdentity, useNested);
        }

        private sealed class ReusedCallbackOrderAdapter : MessageBusTraceAdapter
        {
            private bool _registered;

            internal ReusedCallbackOrderAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void Register(BusTraceOperation operation)
            {
                base.Register(operation);
                if (operation.HandleSlot != 0)
                {
                    return;
                }
                if (_registered)
                {
                    base.Remove(new BusTraceOperation(BusTraceOperationKind.Remove, handleSlot: 1));
                    base.DuplicateRegistration(
                        new BusTraceOperation(
                            BusTraceOperationKind.DuplicateRegistration,
                            handleSlot: 1,
                            sourceHandleSlot: 1
                        )
                    );
                }
                _registered = true;
            }
        }

        private sealed class DuplicateAsCopyAdapter : MessageBusTraceAdapter
        {
            internal DuplicateAsCopyAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void DuplicateRegistration(BusTraceOperation operation) =>
                CopyHandle(operation);
        }

        private sealed class WrongIndependentHandleAdapter : MessageBusTraceAdapter
        {
            internal WrongIndependentHandleAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void Remove(BusTraceOperation operation) =>
                base.Remove(
                    operation.HandleSlot >= 0
                        ? new BusTraceOperation(
                            BusTraceOperationKind.Remove,
                            token: operation.Token,
                            handleSlot: 0
                        )
                        : operation
                );
        }

        private sealed class ThrowOnceInNestedCallbackAdapter : MessageBusTraceAdapter
        {
            private long _throwAtEmission;

            internal ThrowOnceInNestedCallbackAdapter(
                MessageScenario scenario,
                MessageBus bus,
                long throwAtEmission
            )
                : base(scenario, bus, reset: bus.ResetState)
            {
                _throwAtEmission = throwAtEmission;
            }

            protected override void OnCallback(int slot, IMessage message)
            {
                if (Bus.EmissionId == _throwAtEmission)
                {
                    _throwAtEmission = -1;
                    throw new InvalidOperationException(
                        "intentional nested trace callback failure"
                    );
                }
            }
        }

        private sealed class MissingNestedEmitAdapter : MessageBusTraceAdapter
        {
            internal MissingNestedEmitAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void EmitNested(BusTraceOperation operation) { }
        }

        private sealed class IgnoredHandlerActivityAdapter : MessageBusTraceAdapter
        {
            internal IgnoredHandlerActivityAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void SetHandlerActive(int slot, bool active) { }
        }

        private sealed class IgnoredCallbackDisableAdapter : MessageBusTraceAdapter
        {
            internal IgnoredCallbackDisableAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override void DisableFromCallback(int slot) { }
        }

        private sealed class IgnoredForceTrimAdapter : MessageBusTraceAdapter
        {
            internal IgnoredForceTrimAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState) { }

            protected override IMessageBus.TrimResult Trim(bool force) => Bus.Trim(force: false);
        }

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
                    "generator=" + BusTraceSequence.GeneratorVersion,
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
            bool deferReset = false,
            string observationFault = null,
            bool throwAfterEmit = false
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionTicks: 0,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            bus.DiagnosticsMode = false;
            DeferredResetEmitter delayed = deferReset ? new DeferredResetEmitter(bus) : null;
            return new MessageBusTraceAdapter(
                scenario,
                bus,
                dropEmits ? new DropEmitsBus(bus)
                    : observationFault != null || throwAfterEmit
                        ? new FinalValueEmitter(bus, observationFault, throwAfterEmit)
                    : delayed,
                delayed != null ? delayed.RequestReset : bus.ResetState
            );
        }

        private sealed class DiagnosticCalibrationAdapter : MessageBusTraceAdapter
        {
            private readonly MessageKind _kind;
            private MessageBusRegistration _bareRegistration;
            internal int GlobalCalls { get; private set; }
            internal int VetoCalls { get; private set; }

            internal DiagnosticCalibrationAdapter(MessageScenario scenario, MessageBus bus)
                : base(scenario, bus, reset: bus.ResetState)
            {
                _kind = scenario.Kind;
            }

            internal void Configure(string setup)
            {
                if (setup == "global")
                {
                    Token(0)
                        .RegisterGlobalAcceptAll(
                            (IUntargetedMessage _) => RecordGlobalCall(),
                            (InstanceId _, ITargetedMessage __) => RecordGlobalCall(),
                            (InstanceId _, IBroadcastMessage __) => RecordGlobalCall()
                        );
                }
                else if (setup == "bare")
                {
                    MessageHandler handler = new(new InstanceId(9876), Bus) { active = true };
                    _bareRegistration = _kind switch
                    {
                        MessageKind.Untargeted => Bus.RegisterUntargeted<UntargetedPayload>(
                            handler
                        ),
                        MessageKind.Targeted => Bus.RegisterTargeted<TargetedPayload>(
                            new InstanceId(2000),
                            handler
                        ),
                        MessageKind.Broadcast => Bus.RegisterSourcedBroadcast<BroadcastPayload>(
                            new InstanceId(2000),
                            handler
                        ),
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                }
                else if (setup == "veto")
                {
                    switch (_kind)
                    {
                        case MessageKind.Untargeted:
                            Token(0)
                                .RegisterUntargetedInterceptor<UntargetedPayload>(
                                    (ref UntargetedPayload _) =>
                                    {
                                        ++VetoCalls;
                                        return false;
                                    }
                                );
                            break;
                        case MessageKind.Targeted:
                            Token(0)
                                .RegisterTargetedInterceptor<TargetedPayload>(
                                    (ref InstanceId _, ref TargetedPayload __) =>
                                    {
                                        ++VetoCalls;
                                        return false;
                                    }
                                );
                            break;
                        case MessageKind.Broadcast:
                            Token(0)
                                .RegisterBroadcastInterceptor<BroadcastPayload>(
                                    (ref InstanceId _, ref BroadcastPayload __) =>
                                    {
                                        ++VetoCalls;
                                        return false;
                                    }
                                );
                            break;
                    }
                }
            }

            private void RecordGlobalCall()
            {
                ++GlobalCalls;
                // Unrelated and null log messages must not become unmatched observations or exceptions.
                MessagingDebug.Log(LogLevel.Info, null);
                MessagingDebug.Log(LogLevel.Info, "unrelated {0} diagnostic");
                MessagingDebug.Log(LogLevel.Error, "unrelated error");
                MessagingDebug.Log(
                    LogLevel.Warn,
                    "Could not find a matching untargeted broadcast handler noise"
                );
            }

            protected override void DisposeToken(int slot)
            {
                if (slot == 0)
                {
                    switch (_kind)
                    {
                        case MessageKind.Untargeted:
                            Bus.Deregister<UntargetedPayload>(_bareRegistration);
                            break;
                        case MessageKind.Targeted:
                            Bus.Deregister<TargetedPayload>(_bareRegistration);
                            break;
                        case MessageKind.Broadcast:
                            Bus.Deregister<BroadcastPayload>(_bareRegistration);
                            break;
                    }
                }
                base.DisposeToken(slot);
            }
        }

        // Mutate the real ref arguments after production dispatch; never edit observations.
        private sealed class FinalValueEmitter : DelegatingMessageBus
        {
            private readonly string _fault;
            private readonly bool _throwAfter;

            internal FinalValueEmitter(IMessageBus bus, string fault, bool throwAfter)
                : base(bus)
            {
                _fault = fault;
                _throwAfter = throwAfter;
            }

            private void Complete<TMessage>(ref TMessage message)
            {
                if (_fault == "payload")
                {
                    object changed = message switch
                    {
                        MessageBusTraceAdapter.UntargetedPayload value =>
                            new MessageBusTraceAdapter.UntargetedPayload(value.Value + 100),
                        MessageBusTraceAdapter.TargetedPayload value =>
                            new MessageBusTraceAdapter.TargetedPayload(value.Value + 100),
                        MessageBusTraceAdapter.BroadcastPayload value =>
                            new MessageBusTraceAdapter.BroadcastPayload(value.Value + 100),
                        _ => throw new InvalidOperationException("Unsupported trace payload."),
                    };
                    message = (TMessage)changed;
                }
                if (_throwAfter)
                {
                    throw new InvalidOperationException("intentional post-emission failure");
                }
            }

            public override void UntargetedBroadcast<TMessage>(ref TMessage message)
            {
                base.UntargetedBroadcast(ref message);
                Complete(ref message);
            }

            public override void TargetedBroadcast<TMessage>(
                ref InstanceId target,
                ref TMessage message
            )
            {
                base.TargetedBroadcast(ref target, ref message);
                if (_fault == "context")
                {
                    target = new InstanceId(target.Id + 100);
                }
                Complete(ref message);
            }

            public override void SourcedBroadcast<TMessage>(
                ref InstanceId source,
                ref TMessage message
            )
            {
                base.SourcedBroadcast(ref source, ref message);
                if (_fault == "context")
                {
                    source = new InstanceId(source.Id + 100);
                }
                Complete(ref message);
            }
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
