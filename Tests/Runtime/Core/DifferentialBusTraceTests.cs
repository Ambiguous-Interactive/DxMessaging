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
        public void GeneratorVersionPinsKnownSeedPrefix([Values(1, 2, 3, 4, 5, 6, 7)] int version)
        {
            BusTraceSequence sequence = DifferentialBusTrace.Generate(
                MessageScenario.Untargeted(),
                17,
                4,
                generatorVersion: version
            );
            Assert.That(
                BusTraceSequence.GeneratorVersion,
                Is.EqualTo(7),
                "Changing generation requires a new version and a reviewed replay fixture."
            );
            CollectionAssert.AreEqual(
                version == 7
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
            [Values(3, 4, 5, 6, 7)] int version
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
            [Values("reset", "throw", "nested", "disable")] string callback
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
                        callback switch
                        {
                            "throw" => BusTraceOperationKind.EmitWithThrow,
                            "nested" => BusTraceOperationKind.EmitNested,
                            "disable" => BusTraceOperationKind.EmitWithDisable,
                            _ => BusTraceOperationKind.EmitWithReset,
                        },
                        context: otherRoute ? 1 : 0,
                        value: 10,
                        depth: callback == "nested" ? 10 : 0
                    )
                );
                string label =
                    $"[{scenario.Kind}] callback={callback}, otherRoute={otherRoute}: {untriggered}";
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
        [TestCase(8)]
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
                "order" => new EqualPriorityReorderAdapter(scenario, bus),
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
            bool deferReset = false
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
