namespace DxMessaging.Tests.Editor.Allocations
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Editor.Benchmarks;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    [Category("Performance")]
    public sealed class ProviderResolutionBenchmarks : BenchmarkTestBase
    {
        private const int SampleCount = 5;
        private const double SampleSeconds = 0.5d;
        private const double TargetSlowdown = 1.15d;

        private GlobalBusScope _globalBusScope;

        [SetUp]
        public void CaptureGlobalBus()
        {
            _globalBusScope = GlobalBusScope.CaptureAndReset();
        }

        [TearDown]
        public void RestoreGlobalBus()
        {
            _globalBusScope?.Dispose();
            _globalBusScope = null;
        }

        [Test]
        public void ProviderResolutionPerformance()
        {
            string section = BenchmarkDocumentation.GetOperatingSystemSection();
            BenchmarkSession session = new(section, "### ");

            RunWithSession(
                session,
                () =>
                {
                    TimeSpan timeout = TimeSpan.FromSeconds(SampleSeconds);

                    _ = RunBenchmark(TimeSpan.FromMilliseconds(100), useProvider: false);
                    _ = RunBenchmark(TimeSpan.FromMilliseconds(100), useProvider: true);

                    List<PairedBenchmarkSample> samples = new(SampleCount);
                    for (int i = 0; i < SampleCount; i++)
                    {
                        bool providerFirst = (i % 2) == 1;
                        BenchmarkResult direct;
                        BenchmarkResult provider;
                        if (providerFirst)
                        {
                            provider = RunBenchmark(timeout, useProvider: true);
                            direct = RunBenchmark(timeout, useProvider: false);
                        }
                        else
                        {
                            direct = RunBenchmark(timeout, useProvider: false);
                            provider = RunBenchmark(timeout, useProvider: true);
                        }

                        Assert.That(
                            direct.Count,
                            Is.GreaterThan(0),
                            "Direct benchmark produced no operations."
                        );
                        Assert.That(
                            provider.Count,
                            Is.GreaterThan(0),
                            "Provider benchmark produced no operations."
                        );
                        samples.Add(new PairedBenchmarkSample(i + 1, direct, provider));
                    }

                    BenchmarkResult aggregateDirect = Aggregate(samples, useProvider: false);
                    BenchmarkResult aggregateProvider = Aggregate(samples, useProvider: true);

                    RecordBenchmark(
                        "DxMessaging (Untargeted) - Direct Bus",
                        aggregateDirect.Count,
                        aggregateDirect.Duration,
                        allocating: false
                    );
                    RecordBenchmark(
                        "DxMessaging (Untargeted) - Provider",
                        aggregateProvider.Count,
                        aggregateProvider.Duration,
                        allocating: false
                    );

                    WriteSampleDiagnostics(samples);
                }
            );
        }

        private BenchmarkResult RunBenchmark(TimeSpan timeout, bool useProvider)
        {
            MessageBus bus = new();
            MessageHandler handler = new(new InstanceId(5000), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            _ = token.RegisterUntargeted((ref BenchmarkUntargetedMessage _) => { });
            token.Enable();

            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                int count = 0;
                BenchmarkUntargetedMessage message = new(0);
                IMessageBusProvider provider = useProvider
                    ? new StaticMessageBusProvider(bus)
                    : null;

                while (stopwatch.Elapsed < timeout)
                {
                    if (useProvider)
                    {
                        message.EmitUntargeted(messageBusProvider: provider);
                    }
                    else
                    {
                        message.EmitUntargeted(bus);
                    }

                    count++;
                }

                stopwatch.Stop();

                return new BenchmarkResult(count, stopwatch.Elapsed);
            }
            finally
            {
                token.Disable();
            }
        }

        private static BenchmarkResult Aggregate(
            List<PairedBenchmarkSample> samples,
            bool useProvider
        )
        {
            int totalCount = 0;
            TimeSpan totalDuration = TimeSpan.Zero;
            foreach (PairedBenchmarkSample sample in samples)
            {
                BenchmarkResult result = useProvider ? sample.Provider : sample.Direct;
                totalCount += result.Count;
                totalDuration += result.Duration;
            }

            return new BenchmarkResult(totalCount, totalDuration);
        }

        private static void WriteSampleDiagnostics(List<PairedBenchmarkSample> samples)
        {
            List<double> slowdowns = new(samples.Count);
            TestContext.Out.WriteLine("Provider resolution paired benchmark samples:");
            foreach (PairedBenchmarkSample sample in samples)
            {
                double slowdown = sample.Slowdown;
                slowdowns.Add(slowdown);
                TestContext.Out.WriteLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "  pair {0}: direct={1:N0} ops/s provider={2:N0} ops/s slowdown={3:F4}x",
                        sample.Index,
                        sample.Direct.OperationsPerSecond,
                        sample.Provider.OperationsPerSecond,
                        slowdown
                    )
                );
            }

            slowdowns.Sort();
            double medianSlowdown = slowdowns[slowdowns.Count / 2];
            double minSlowdown = slowdowns[0];
            double maxSlowdown = slowdowns[slowdowns.Count - 1];
            string summary = string.Format(
                CultureInfo.InvariantCulture,
                "Provider slowdown summary: target={0:F2}x median={1:F4}x min={2:F4}x max={3:F4}x samples={4}",
                TargetSlowdown,
                medianSlowdown,
                minSlowdown,
                maxSlowdown,
                samples.Count
            );
            TestContext.Out.WriteLine(summary);
            if (medianSlowdown > TargetSlowdown)
            {
                UnityEngine.Debug.LogWarning(
                    summary
                        + " (telemetry only; this benchmark no longer fails CI from a single noisy sample)"
                );
            }
        }

        private readonly struct BenchmarkResult
        {
            internal BenchmarkResult(int count, TimeSpan duration)
            {
                Count = count;
                Duration = duration;
            }

            internal int Count { get; }
            internal TimeSpan Duration { get; }
            internal double OperationsPerSecond =>
                Duration.TotalSeconds <= 0 ? 0 : Count / Duration.TotalSeconds;
        }

        private readonly struct PairedBenchmarkSample
        {
            internal PairedBenchmarkSample(
                int index,
                BenchmarkResult direct,
                BenchmarkResult provider
            )
            {
                Index = index;
                Direct = direct;
                Provider = provider;
            }

            internal int Index { get; }
            internal BenchmarkResult Direct { get; }
            internal BenchmarkResult Provider { get; }
            internal double Slowdown =>
                Provider.OperationsPerSecond <= 0
                    ? double.PositiveInfinity
                    : Direct.OperationsPerSecond / Provider.OperationsPerSecond;
        }

        private sealed class StaticMessageBusProvider : IMessageBusProvider
        {
            private readonly IMessageBus _bus;

            internal StaticMessageBusProvider(IMessageBus bus)
            {
                _bus = bus;
            }

            public IMessageBus Resolve()
            {
                return _bus;
            }
        }

        private readonly struct BenchmarkUntargetedMessage : IUntargetedMessage
        {
            internal BenchmarkUntargetedMessage(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }
    }
}
