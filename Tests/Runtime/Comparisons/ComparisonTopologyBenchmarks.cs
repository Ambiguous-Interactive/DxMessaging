#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Exact internal workloads for comparison shapes absent from the dispatch table.
    /// These use independent registrations, never the public comparison bridge. Their
    /// results have a separate prefix and do not replace a public-contract comparison row.
    /// </summary>
    [Category("Performance"), Category("PerfTopologyTwin")]
    public sealed class ComparisonTopologyBenchmarks
    {
        internal static readonly ComparisonScenario[] Scenarios =
        {
            ComparisonScenario.KeyedToOneOfMany,
            ComparisonScenario.PriorityOrderedDispatch,
            ComparisonScenario.FilteredDispatch,
            ComparisonScenario.PostProcessingDispatch,
            ComparisonScenario.InterceptedPostProcessingDispatch,
            ComparisonScenario.SubscribeUnsubscribeChurn,
            ComparisonScenario.StructMessageNoBoxing,
        };

        internal static string RowKey(ComparisonScenario scenario) =>
            $"InternalComparisonTwin_{ComparisonScenarios.Key(scenario)}";

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (ComparisonScenario scenario in Scenarios)
            {
                yield return new TestCaseData(scenario).SetName(RowKey(scenario));
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void Benchmark(ComparisonScenario scenario)
        {
            using Workload workload = new(scenario);
            int warmup = ComparisonScenarios.WarmupEmits(scenario);
            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () => workload.EmitMany(warmup),
                () =>
                {
                    workload.EmitMany(BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );
            AssertAccounting(
                scenario,
                warmup + measurement.TotalEmittedOperations,
                workload.Progress
            );
            workload.Dispose();
            AssertClean(workload.Bus, scenario.ToString());
            DispatchBenchmarkResult result = DispatchBenchmarkResult.ForEmitScenario(
                RowKey(scenario),
                -1,
                measurement.OperationsPerSecond,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                measurement.ElapsedSeconds * 1000d
            );
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        internal static void AssertAccounting(
            ComparisonScenario scenario,
            long totalOperations,
            long observed
        ) =>
            Assert.AreEqual(
                ComparisonScenarios.ExpectedInvocationsPerOperation(scenario) * totalOperations,
                observed,
                $"[{scenario}] Callbacks (or completed churn cycles) must match every warm-up, timed, and allocation-probe operation."
            );

        internal static void AssertClean(MessageBus bus, string label) =>
            CollectionAssert.AreEqual(
                new[] { "bus:0:0:0:0:0:0", "diagnostics:False" },
                DispatchThroughputBenchmarks.CaptureTopologyForContract(
                    bus,
                    Array.Empty<MessageRegistrationToken>()
                ),
                $"[{label}] Teardown must clear all six registration counters."
            );

#pragma warning disable RCS1242 // Same readonly-reference handlers as the public comparison.
        internal sealed class Workload : IDisposable
        {
            internal readonly MessageBus Bus = new() { DiagnosticsMode = false };
            internal readonly List<MessageRegistrationToken> Tokens = new();
            internal readonly MessageRegistrationToken Token;
            private readonly ComparisonScenario _scenario;
            private readonly MessageHandler.FastHandler<SimpleUntargetedMessage> _handler;
            private SimpleUntargetedMessage _untargeted;
            private SimpleTargetedMessage _targeted;
            private ComparisonStructPayload _payload = new(1);
            private InstanceId _target = new(41000);
            internal long Progress { get; private set; }

            /*
                SYNC: DxMessagingBridge.Prepare is the public-contract workload.
                Keep this independent twin exact; ComparisonDispatchTopologyTests checks both.
            */
            internal Workload(ComparisonScenario scenario)
            {
                _scenario = scenario;
                Token = AddToken();
                _handler = Count;
                switch (scenario)
                {
                    case ComparisonScenario.KeyedToOneOfMany:
                        for (int index = 0; index < 16; index++)
                        {
                            _ = Token.RegisterTargeted<SimpleTargetedMessage>(
                                new InstanceId(41000 + index),
                                CountTargeted
                            );
                        }
                        break;
                    case ComparisonScenario.PriorityOrderedDispatch:
                        for (int priority = 0; priority < 4; priority++)
                        {
                            _ = Token.RegisterUntargeted<SimpleUntargetedMessage>(
                                _handler,
                                priority
                            );
                        }
                        break;
                    case ComparisonScenario.FilteredDispatch:
                        _ = Token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(Allow);
                        _ = Token.RegisterUntargeted<SimpleUntargetedMessage>(_handler);
                        break;
                    case ComparisonScenario.PostProcessingDispatch:
                        _ = Token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                            PostProcess
                        );
                        _ = Token.RegisterUntargeted<SimpleUntargetedMessage>(_handler);
                        break;
                    case ComparisonScenario.InterceptedPostProcessingDispatch:
                        _ = Token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(Allow);
                        _ = Token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                            PostProcess
                        );
                        _ = Token.RegisterUntargeted<SimpleUntargetedMessage>(_handler);
                        break;
                    case ComparisonScenario.SubscribeUnsubscribeChurn:
                        break;
                    case ComparisonScenario.StructMessageNoBoxing:
                        _ = Token.RegisterUntargeted<ComparisonStructPayload>(CountStruct);
                        break;
                    default:
                        Dispose();
                        throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
                }
            }

            internal MessageRegistrationToken AddToken()
            {
                MessageHandler handler = new(new InstanceId(42000 + Tokens.Count), Bus)
                {
                    active = true,
                };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, Bus);
                token.DiagnosticMode = false;
                token.Enable();
                Tokens.Add(token);
                return token;
            }

            internal string[] CaptureTopology() =>
                DispatchThroughputBenchmarks.CaptureTopologyForContract(Bus, Tokens);

            internal void EmitMany(int count)
            {
                // Select the workload outside the operation loop, as internal dispatch rows do.
                switch (_scenario)
                {
                    case ComparisonScenario.KeyedToOneOfMany:
                        for (int index = 0; index < count; index++)
                        {
                            Bus.TargetedBroadcast(ref _target, ref _targeted);
                        }
                        break;
                    case ComparisonScenario.StructMessageNoBoxing:
                        for (int index = 0; index < count; index++)
                        {
                            Bus.UntargetedBroadcast(ref _payload);
                        }
                        break;
                    case ComparisonScenario.SubscribeUnsubscribeChurn:
                        for (int index = 0; index < count; index++)
                        {
                            MessageRegistrationHandle handle =
                                Token.RegisterUntargeted<SimpleUntargetedMessage>(_handler);
                            Token.RemoveRegistration(handle);
                            Progress++;
                        }
                        break;
                    default:
                        for (int index = 0; index < count; index++)
                        {
                            Bus.UntargetedBroadcast(ref _untargeted);
                        }
                        break;
                }
            }

            public void Dispose()
            {
                for (int index = Tokens.Count - 1; 0 <= index; index--)
                {
                    Tokens[index].UnregisterAll();
                    Tokens[index].Dispose();
                }
                Tokens.Clear();
            }

            private void Count(in SimpleUntargetedMessage message) => Progress++;

            private void CountTargeted(in SimpleTargetedMessage message) => Progress++;

            private void CountStruct(in ComparisonStructPayload message) => Progress++;

            private static bool Allow(ref SimpleUntargetedMessage message) => true;

            private void PostProcess(in SimpleUntargetedMessage message) { }
        }
#pragma warning restore RCS1242
    }
}
#endif
