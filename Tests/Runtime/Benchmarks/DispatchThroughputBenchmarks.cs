#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class PerformanceAttribute : CategoryAttribute
    {
        public PerformanceAttribute()
            : base("Performance") { }
    }

    public enum DispatchBenchmarkScenario
    {
        UntargetedFloodOneHandler,
        UntargetedFloodFourHandlersOnePriority,
        UntargetedFloodFourHandlersFourPriorities,
        TargetedFloodOneListener,
        TargetedFloodSixteenListeners,
        BroadcastFloodOneHandler,
        InterceptorHeavyFourInterceptors,
        PostProcessingHeavyFourPostProcessors,
        RegistrationFlood1000TypesFromColdBus,
    }

    public sealed class DispatchThroughputBenchmarks
    {
        private const string BaselineOutputEnvVar = "DX_PERF_BASELINE";
        private const string BaselineModeEnvVar = "DX_PERF_BASELINE_MODE";
        private const string PackageName = "com.wallstop-studios.dxmessaging";
        private const string BaselineCsvHeader =
            "scenario,platform,commit,runIndex,emitsPerSecond,allocatedBytesDelta,wallClockMs";
        private static readonly InstanceId Target = new(31001);
        private static readonly InstanceId Source = new(31002);
        private static Action<MessageRegistrationToken>[] _registrationFloodBuilders;

        [Test, Performance, Category("PerfBench")]
        public void UntargetedFloodOneHandler()
        {
            _ = RunScenario(DispatchBenchmarkScenario.UntargetedFloodOneHandler);
        }

        [Test, Performance, Category("PerfBench")]
        public void UntargetedFloodFourHandlersOnePriority()
        {
            _ = RunScenario(DispatchBenchmarkScenario.UntargetedFloodFourHandlersOnePriority);
        }

        [Test, Performance, Category("PerfBench")]
        public void UntargetedFloodFourHandlersFourPriorities()
        {
            _ = RunScenario(DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities);
        }

        [Test, Performance, Category("PerfBench")]
        public void TargetedFloodOneListener()
        {
            _ = RunScenario(DispatchBenchmarkScenario.TargetedFloodOneListener);
        }

        [Test, Performance, Category("PerfBench")]
        public void TargetedFloodSixteenListeners()
        {
            _ = RunScenario(DispatchBenchmarkScenario.TargetedFloodSixteenListeners);
        }

        [Test, Performance, Category("PerfBench")]
        public void BroadcastFloodOneHandler()
        {
            _ = RunScenario(DispatchBenchmarkScenario.BroadcastFloodOneHandler);
        }

        [Test, Performance, Category("PerfBench")]
        public void InterceptorHeavyFourInterceptors()
        {
            _ = RunScenario(DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors);
        }

        [Test, Performance, Category("PerfBench")]
        public void PostProcessingHeavyFourPostProcessors()
        {
            _ = RunScenario(DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors);
        }

        [Test, Performance, Category("PerfBench")]
        public void RegistrationFlood1000TypesFromColdBus()
        {
            _ = RunScenario(DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus);
        }

        [Test, Explicit, Performance, Category("PerfBaseline")]
        public void UpdateDispatchThroughputBaseline()
        {
            string outputPath = ResolveBaselineOutputPath();
            bool replaceAllRows = string.Equals(
                Environment.GetEnvironmentVariable(BaselineModeEnvVar),
                "replace",
                StringComparison.OrdinalIgnoreCase
            );

            List<DispatchBenchmarkResult> results = new();
            foreach (
                DispatchBenchmarkScenario scenario in Enum.GetValues(
                    typeof(DispatchBenchmarkScenario)
                )
            )
            {
                results.Add(RunScenario(scenario));
            }

            WriteBaselineRows(outputPath, results, replaceAllRows);
            TestContext.Out.WriteLine($"Updated performance baseline: {outputPath}");
        }

        public static DispatchBenchmarkResult RunScenario(
            DispatchBenchmarkScenario scenario,
            bool logResult = true
        )
        {
            DispatchBenchmarkResult result =
                scenario == DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus
                    ? MeasureRegistrationFlood()
                    : MeasureEmitScenario(scenario);
            if (logResult)
            {
                Debug.Log(result.ToStructuredLog());
                TestContext.Out.WriteLine(result.ToCsvRow());
            }

            return result;
        }

        public static string GetScenarioName(DispatchBenchmarkScenario scenario)
        {
            return DispatchBenchmarkScenarios.Key(scenario);
        }

        private static DispatchBenchmarkResult MeasureEmitScenario(
            DispatchBenchmarkScenario scenario
        )
        {
            using BenchmarkRegistrationScope scope = new();
            InvocationCounter handlerInvocations = new();
            ConfigureScenario(scope, scenario, handlerInvocations);

            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () =>
                    EmitMany(scope.Bus, scenario, DispatchBenchmarkScenarios.WarmupEmits(scenario)),
                () =>
                {
                    EmitMany(scope.Bus, scenario, BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );

            Assert.Greater(
                handlerInvocations.Count,
                0,
                "Benchmark scenario did not invoke handlers."
            );
            return DispatchBenchmarkResult.ForEmitScenario(
                GetScenarioName(scenario),
                runIndex: -1,
                measurement.OperationsPerSecond,
                measurement.AllocatedBytesDelta,
                measurement.ElapsedSeconds * 1000d
            );
        }

        private static DispatchBenchmarkResult MeasureRegistrationFlood()
        {
            Action<MessageRegistrationToken>[] builders = GetRegistrationFloodBuilders();
            long beforeAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            long startTimestamp = Stopwatch.GetTimestamp();
            using (BenchmarkRegistrationScope scope = new())
            {
                for (int index = 0; index < builders.Length; index++)
                {
                    builders[index](scope.PrimaryToken);
                }
            }
            long endTimestamp = Stopwatch.GetTimestamp();
            long afterAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();

            return DispatchBenchmarkResult.ForRegistrationScenario(
                GetScenarioName(DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus),
                runIndex: -1,
                afterAllocatedBytes - beforeAllocatedBytes,
                TimestampDeltaToSeconds(startTimestamp, endTimestamp) * 1000d
            );
        }

        private static double TimestampDeltaToSeconds(long startTimestamp, long endTimestamp)
        {
            return (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
        }

        private static void ConfigureScenario(
            BenchmarkRegistrationScope scope,
            DispatchBenchmarkScenario scenario,
            InvocationCounter handlerInvocations
        )
        {
            switch (scenario)
            {
                case DispatchBenchmarkScenario.UntargetedFloodOneHandler:
                    RegisterUntargeted(scope, handlerInvocations, 0);
                    return;
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersOnePriority:
                    for (int index = 0; index < 4; index++)
                    {
                        RegisterUntargeted(scope, handlerInvocations, 0);
                    }
                    return;
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities:
                    for (int priority = 0; priority < 4; priority++)
                    {
                        RegisterUntargeted(scope, handlerInvocations, priority);
                    }
                    return;
                case DispatchBenchmarkScenario.TargetedFloodOneListener:
                    RegisterTargeted(scope, handlerInvocations, 0);
                    return;
                case DispatchBenchmarkScenario.TargetedFloodSixteenListeners:
                    for (int index = 0; index < 16; index++)
                    {
                        RegisterTargeted(scope, handlerInvocations, 0);
                    }
                    return;
                case DispatchBenchmarkScenario.BroadcastFloodOneHandler:
                    RegisterBroadcast(scope, handlerInvocations, 0);
                    return;
                case DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors:
                    for (int priority = 0; priority < 4; priority++)
                    {
                        _ =
                            scope.PrimaryToken.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                                AllowUntargeted,
                                priority
                            );
                    }
                    RegisterUntargeted(scope, handlerInvocations, 0);
                    return;
                case DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors:
                    for (int priority = 0; priority < 4; priority++)
                    {
                        _ =
                            scope.PrimaryToken.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                                CountPostProcessed,
                                priority
                            );
                    }
                    RegisterUntargeted(scope, handlerInvocations, 0);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }

            void CountPostProcessed(ref SimpleUntargetedMessage message)
            {
                handlerInvocations.Increment();
            }
        }

        private static void RegisterUntargeted(
            BenchmarkRegistrationScope scope,
            InvocationCounter handlerInvocations,
            int priority
        )
        {
            MessageRegistrationToken token = scope.CreateToken();
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (ref SimpleUntargetedMessage message) => handlerInvocations.Increment(),
                priority
            );
        }

        private static void RegisterTargeted(
            BenchmarkRegistrationScope scope,
            InvocationCounter handlerInvocations,
            int priority
        )
        {
            MessageRegistrationToken token = scope.CreateToken();
            _ = token.RegisterTargeted<SimpleTargetedMessage>(
                Target,
                (ref SimpleTargetedMessage message) => handlerInvocations.Increment(),
                priority
            );
        }

        private static void RegisterBroadcast(
            BenchmarkRegistrationScope scope,
            InvocationCounter handlerInvocations,
            int priority
        )
        {
            MessageRegistrationToken token = scope.CreateToken();
            _ = token.RegisterBroadcast<SimpleBroadcastMessage>(
                Source,
                (ref SimpleBroadcastMessage message) => handlerInvocations.Increment(),
                priority
            );
        }

        private static void EmitMany(MessageBus bus, DispatchBenchmarkScenario scenario, int count)
        {
            switch (scenario)
            {
                case DispatchBenchmarkScenario.UntargetedFloodOneHandler:
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersOnePriority:
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities:
                case DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors:
                case DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors:
                    SimpleUntargetedMessage untargeted = new();
                    for (int index = 0; index < count; index++)
                    {
                        bus.UntargetedBroadcast(ref untargeted);
                    }
                    return;
                case DispatchBenchmarkScenario.TargetedFloodOneListener:
                case DispatchBenchmarkScenario.TargetedFloodSixteenListeners:
                    SimpleTargetedMessage targeted = new();
                    InstanceId target = Target;
                    for (int index = 0; index < count; index++)
                    {
                        bus.TargetedBroadcast(ref target, ref targeted);
                    }
                    return;
                case DispatchBenchmarkScenario.BroadcastFloodOneHandler:
                    SimpleBroadcastMessage broadcast = new();
                    InstanceId source = Source;
                    for (int index = 0; index < count; index++)
                    {
                        bus.SourcedBroadcast(ref source, ref broadcast);
                    }
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }
        }

        private static bool AllowUntargeted(ref SimpleUntargetedMessage message)
        {
            return true;
        }

        private static Action<MessageRegistrationToken>[] GetRegistrationFloodBuilders()
        {
            if (_registrationFloodBuilders != null)
            {
                return _registrationFloodBuilders;
            }

            MethodInfo builderMethod = typeof(DispatchThroughputBenchmarks).GetMethod(
                nameof(RegisterFloodMessage),
                BindingFlags.NonPublic | BindingFlags.Static
            );
            if (builderMethod == null)
            {
                throw new MissingMethodException(nameof(RegisterFloodMessage));
            }

            Type[] markerTypes = RegistrationFloodMarkerTypes.All;
            List<Action<MessageRegistrationToken>> builders = new(capacity: 1000);
            for (int outerIndex = 0; outerIndex < markerTypes.Length; outerIndex++)
            {
                for (int innerIndex = 0; innerIndex < markerTypes.Length; innerIndex++)
                {
                    Type markerType = typeof(RegistrationFloodMarker<,>).MakeGenericType(
                        markerTypes[outerIndex],
                        markerTypes[innerIndex]
                    );
                    MethodInfo closedMethod = builderMethod.MakeGenericMethod(markerType);
                    builders.Add(
                        (Action<MessageRegistrationToken>)
                            Delegate.CreateDelegate(
                                typeof(Action<MessageRegistrationToken>),
                                closedMethod
                            )
                    );
                    if (builders.Count == 1000)
                    {
                        break;
                    }
                }

                if (builders.Count == 1000)
                {
                    break;
                }
            }

            if (builders.Count < 1000)
            {
                throw new InvalidOperationException(
                    $"Expected at least 1000 marker types for the registration flood, found {builders.Count}."
                );
            }

            _registrationFloodBuilders = builders.ToArray();
            return _registrationFloodBuilders;
        }

        private static string ResolveBaselineOutputPath()
        {
            string configuredPath = Environment.GetEnvironmentVariable(BaselineOutputEnvVar);
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = ".artifacts/perf-baseline.csv";
            }

            if (Path.IsPathRooted(configuredPath))
            {
                return configuredPath;
            }

            string packageRoot = ResolvePackageRoot();
            string baseDirectory = packageRoot ?? ResolveUnityProjectRoot();
            return Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
        }

        internal static string ResolvePackageRoot()
        {
#if UNITY_EDITOR
            string packageInfoRoot = ResolvePackageInfoRoot(
                typeof(DispatchThroughputBenchmarks).Assembly
            );
            if (packageInfoRoot != null)
            {
                return packageInfoRoot;
            }

            packageInfoRoot = ResolvePackageInfoRoot(typeof(MessageBus).Assembly);
            if (packageInfoRoot != null)
            {
                return packageInfoRoot;
            }
#endif

            string[] roots = { Directory.GetCurrentDirectory(), Application.dataPath };
            for (int index = 0; index < roots.Length; index++)
            {
                string packageRoot = FindPackageRoot(roots[index]);
                if (packageRoot != null)
                {
                    return packageRoot;
                }
            }

            return null;
        }

#if UNITY_EDITOR
        private static string ResolvePackageInfoRoot(Assembly assembly)
        {
            UnityEditor.PackageManager.PackageInfo packageInfo =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
            if (
                packageInfo != null
                && string.Equals(packageInfo.name, PackageName, StringComparison.Ordinal)
                && Directory.Exists(packageInfo.resolvedPath)
            )
            {
                return FindPackageRoot(packageInfo.resolvedPath);
            }

            return null;
        }
#endif

        private static string FindPackageRoot(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                return null;
            }

            DirectoryInfo current = new(startDirectory);
            while (current != null)
            {
                if (IsPackageRoot(current.FullName))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private static bool IsPackageRoot(string directory)
        {
            string packageJsonPath = Path.Combine(directory, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                return false;
            }

            string packageJson = File.ReadAllText(packageJsonPath);
            return Regex.IsMatch(packageJson, $"\"name\"\\s*:\\s*\"{Regex.Escape(PackageName)}\"");
        }

        private static string ResolveUnityProjectRoot()
        {
            string assetsPath = Application.dataPath;
            if (string.IsNullOrWhiteSpace(assetsPath))
            {
                return Directory.GetCurrentDirectory();
            }

            return Directory.GetParent(assetsPath)?.FullName ?? Directory.GetCurrentDirectory();
        }

        private static void WriteBaselineRows(
            string outputPath,
            IReadOnlyList<DispatchBenchmarkResult> results,
            bool replaceAllRows
        )
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            List<string> rows = replaceAllRows
                ? new List<string>()
                : ReadExistingBaselineRows(outputPath);
            for (int index = 0; index < results.Count; index++)
            {
                DispatchBenchmarkResult result = results[index];
                RemoveMatchingBaselineRow(rows, result);
                rows.Add(result.ToCsvRow());
            }

            rows.Sort(CompareBaselineRows);

            StringBuilder builder = new();
            builder.AppendLine(BaselineCsvHeader);
            for (int index = 0; index < rows.Count; index++)
            {
                builder.AppendLine(rows[index]);
            }

            File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(false));
        }

        private static List<string> ReadExistingBaselineRows(string outputPath)
        {
            List<string> rows = new();
            if (!File.Exists(outputPath))
            {
                return rows;
            }

            string[] lines = File.ReadAllLines(outputPath);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (
                    string.IsNullOrWhiteSpace(line)
                    || line.StartsWith("scenario,", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                rows.Add(line);
            }

            return rows;
        }

        private static void RemoveMatchingBaselineRow(
            List<string> rows,
            DispatchBenchmarkResult result
        )
        {
            for (int index = rows.Count - 1; index >= 0; index--)
            {
                string[] fields = ParseCsvFields(rows[index]);
                if (
                    fields.Length >= 3
                    && string.Equals(fields[0], result.Scenario, StringComparison.Ordinal)
                    && string.Equals(fields[1], result.Platform, StringComparison.Ordinal)
                    && string.Equals(fields[2], result.Commit, StringComparison.OrdinalIgnoreCase)
                )
                {
                    rows.RemoveAt(index);
                }
            }
        }

        private static int CompareBaselineRows(string left, string right)
        {
            string[] leftFields = ParseCsvFields(left);
            string[] rightFields = ParseCsvFields(right);
            for (int index = 2; index >= 0; index--)
            {
                string leftValue = index < leftFields.Length ? leftFields[index] : string.Empty;
                string rightValue = index < rightFields.Length ? rightFields[index] : string.Empty;
                int comparison = string.CompareOrdinal(leftValue, rightValue);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return string.CompareOrdinal(left, right);
        }

        private static string[] ParseCsvFields(string line)
        {
            List<string> fields = new();
            StringBuilder builder = new();
            bool inQuotes = false;

            for (int index = 0; index < line.Length; index++)
            {
                char value = line[index];
                if (value == '"')
                {
                    if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        builder.Append('"');
                        index++;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    continue;
                }

                if (value == ',' && !inQuotes)
                {
                    fields.Add(builder.ToString());
                    builder.Clear();
                    continue;
                }

                builder.Append(value);
            }

            fields.Add(builder.ToString());
            return fields.ToArray();
        }

        private static void RegisterFloodMessage<TMarker>(MessageRegistrationToken token)
        {
            _ = token.RegisterUntargeted<RegistrationFloodMessage<TMarker>>(NoOpFloodHandler);
        }

        private static void NoOpFloodHandler<TMarker>(
            ref RegistrationFloodMessage<TMarker> message
        ) { }

        private readonly struct RegistrationFloodMessage<TMarker>
            : DxMessaging.Core.Messages.IUntargetedMessage { }

        private readonly struct RegistrationFloodMarker<TOuter, TInner> { }

        private static class RegistrationFloodMarkerTypes
        {
            public static readonly Type[] All =
            {
                typeof(Marker00),
                typeof(Marker01),
                typeof(Marker02),
                typeof(Marker03),
                typeof(Marker04),
                typeof(Marker05),
                typeof(Marker06),
                typeof(Marker07),
                typeof(Marker08),
                typeof(Marker09),
                typeof(Marker10),
                typeof(Marker11),
                typeof(Marker12),
                typeof(Marker13),
                typeof(Marker14),
                typeof(Marker15),
                typeof(Marker16),
                typeof(Marker17),
                typeof(Marker18),
                typeof(Marker19),
                typeof(Marker20),
                typeof(Marker21),
                typeof(Marker22),
                typeof(Marker23),
                typeof(Marker24),
                typeof(Marker25),
                typeof(Marker26),
                typeof(Marker27),
                typeof(Marker28),
                typeof(Marker29),
                typeof(Marker30),
                typeof(Marker31),
            };

            private readonly struct Marker00 { }

            private readonly struct Marker01 { }

            private readonly struct Marker02 { }

            private readonly struct Marker03 { }

            private readonly struct Marker04 { }

            private readonly struct Marker05 { }

            private readonly struct Marker06 { }

            private readonly struct Marker07 { }

            private readonly struct Marker08 { }

            private readonly struct Marker09 { }

            private readonly struct Marker10 { }

            private readonly struct Marker11 { }

            private readonly struct Marker12 { }

            private readonly struct Marker13 { }

            private readonly struct Marker14 { }

            private readonly struct Marker15 { }

            private readonly struct Marker16 { }

            private readonly struct Marker17 { }

            private readonly struct Marker18 { }

            private readonly struct Marker19 { }

            private readonly struct Marker20 { }

            private readonly struct Marker21 { }

            private readonly struct Marker22 { }

            private readonly struct Marker23 { }

            private readonly struct Marker24 { }

            private readonly struct Marker25 { }

            private readonly struct Marker26 { }

            private readonly struct Marker27 { }

            private readonly struct Marker28 { }

            private readonly struct Marker29 { }

            private readonly struct Marker30 { }

            private readonly struct Marker31 { }
        }

        private sealed class InvocationCounter
        {
            public int Count { get; private set; }

            public void Increment()
            {
                Count++;
            }
        }

        private sealed class BenchmarkRegistrationScope : IDisposable
        {
            private readonly List<MessageRegistrationToken> _tokens = new();
            private int _nextOwner = 32000;

            public BenchmarkRegistrationScope()
            {
                Bus = new MessageBus();
                PrimaryToken = CreateToken();
            }

            public MessageBus Bus { get; }

            public MessageRegistrationToken PrimaryToken { get; }

            public MessageRegistrationToken CreateToken()
            {
                MessageHandler handler = new(new InstanceId(_nextOwner++), Bus) { active = true };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, Bus);
                token.Enable();
                _tokens.Add(token);
                return token;
            }

            public void Dispose()
            {
                for (int index = _tokens.Count - 1; index >= 0; index--)
                {
                    _tokens[index].UnregisterAll();
                    _tokens[index].Dispose();
                }
            }
        }
    }

    public readonly struct DispatchBenchmarkResult
    {
        private DispatchBenchmarkResult(
            string scenario,
            string platform,
            string commit,
            int runIndex,
            double emitsPerSecond,
            long allocatedBytesDelta,
            double wallClockMs,
            bool isRegistrationScenario
        )
        {
            Scenario = scenario;
            Platform = platform;
            Commit = commit;
            RunIndex = runIndex;
            EmitsPerSecond = emitsPerSecond;
            AllocatedBytesDelta = allocatedBytesDelta;
            WallClockMs = wallClockMs;
            IsRegistrationScenario = isRegistrationScenario;
        }

        public string Scenario { get; }

        public string Platform { get; }

        public string Commit { get; }

        public int RunIndex { get; }

        public double EmitsPerSecond { get; }

        public long AllocatedBytesDelta { get; }

        public double WallClockMs { get; }

        public bool IsRegistrationScenario { get; }

        public static DispatchBenchmarkResult ForEmitScenario(
            string scenario,
            int runIndex,
            double emitsPerSecond,
            long allocatedBytesDelta,
            double wallClockMs
        )
        {
            return new DispatchBenchmarkResult(
                scenario,
                ResolvePlatform(),
                ResolveCommit(),
                runIndex,
                emitsPerSecond,
                allocatedBytesDelta,
                wallClockMs,
                isRegistrationScenario: false
            );
        }

        public static DispatchBenchmarkResult ForRegistrationScenario(
            string scenario,
            int runIndex,
            long allocatedBytesDelta,
            double wallClockMs
        )
        {
            return new DispatchBenchmarkResult(
                scenario,
                ResolvePlatform(),
                ResolveCommit(),
                runIndex,
                emitsPerSecond: 0,
                allocatedBytesDelta,
                wallClockMs,
                isRegistrationScenario: true
            );
        }

        public string ToCsvRow()
        {
            return string.Join(
                ",",
                EscapeCsv(Scenario),
                EscapeCsv(Platform),
                EscapeCsv(Commit),
                RunIndex.ToString(CultureInfo.InvariantCulture),
                EmitsPerSecond.ToString("F3", CultureInfo.InvariantCulture),
                AllocatedBytesDelta.ToString(CultureInfo.InvariantCulture),
                WallClockMs.ToString("F3", CultureInfo.InvariantCulture)
            );
        }

        public string ToStructuredLog()
        {
            return "{"
                + $"scenario:\"{Scenario}\", "
                + $"platform:\"{Platform}\", "
                + $"commit:\"{Commit}\", "
                + $"runIndex:{RunIndex.ToString(CultureInfo.InvariantCulture)}, "
                + $"emitsPerSec:{EmitsPerSecond.ToString("F3", CultureInfo.InvariantCulture)}, "
                + $"allocatedBytesDelta:{AllocatedBytesDelta.ToString(CultureInfo.InvariantCulture)}, "
                + $"wallClockMs:{WallClockMs.ToString("F3", CultureInfo.InvariantCulture)}"
                + "}";
        }

        private static string ResolvePlatform()
        {
            return $"{ResolveExecutionTarget()} {ResolveScriptingBackend()} {ResolveArchitecture()} {ResolveBuildConfiguration()} ({Application.platform}; Unity {Application.unityVersion})";
        }

        private static string ResolveExecutionTarget()
        {
#if UNITY_EDITOR
            return UnityEngine.Application.isPlaying ? "Editor PlayMode" : "Editor EditMode";
#elif UNITY_STANDALONE
            return "Standalone";
#else
            return Application.platform.ToString();
#endif
        }

        private static string ResolveScriptingBackend()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#elif ENABLE_MONO
            return "Mono";
#else
            return Type.GetType("Mono.Runtime", throwOnError: false) == null
                ? "UnknownBackend"
                : "Mono";
#endif
        }

        private static string ResolveArchitecture()
        {
            return IntPtr.Size == 8 ? "x64" : "x86";
        }

        private static string ResolveBuildConfiguration()
        {
#if UNITY_EDITOR
            return
                UnityEditor.Compilation.CompilationPipeline.codeOptimization
                == UnityEditor.Compilation.CodeOptimization.Release
                ? "Release"
                : "Debug";
#else
            return Debug.isDebugBuild ? "Debug" : "Release";
#endif
        }

        private static string ResolveCommit()
        {
            string commit = Environment.GetEnvironmentVariable("DX_PERF_COMMIT");
            if (!string.IsNullOrWhiteSpace(commit))
            {
                return commit;
            }

            commit = Environment.GetEnvironmentVariable("GITHUB_SHA");
            if (!string.IsNullOrWhiteSpace(commit))
            {
                return commit;
            }

            commit = ResolveGitHeadCommit(DispatchThroughputBenchmarks.ResolvePackageRoot());
            return string.IsNullOrWhiteSpace(commit) ? "local" : commit;
        }

        private static string ResolveGitHeadCommit(string packageRoot)
        {
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                return null;
            }

            string gitPath = Path.Combine(packageRoot, ".git");
            if (File.Exists(gitPath))
            {
                string gitFile = File.ReadAllText(gitPath).Trim();
                const string GitDirPrefix = "gitdir:";
                if (gitFile.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    gitPath = gitFile.Substring(GitDirPrefix.Length).Trim();
                    if (!Path.IsPathRooted(gitPath))
                    {
                        gitPath = Path.GetFullPath(Path.Combine(packageRoot, gitPath));
                    }
                }
            }

            string headPath = Path.Combine(gitPath, "HEAD");
            if (!File.Exists(headPath))
            {
                return null;
            }

            string head = File.ReadAllText(headPath).Trim();
            string commonGitPath = ResolveCommonGitPath(gitPath);
            const string RefPrefix = "ref:";
            if (!head.StartsWith(RefPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(head) ? null : head;
            }

            string refName = head.Substring(RefPrefix.Length).Trim();
            string commit =
                ReadGitRefCommit(gitPath, refName) ?? ReadGitRefCommit(commonGitPath, refName);
            return string.IsNullOrWhiteSpace(commit) ? null : commit;
        }

        private static string ResolveCommonGitPath(string gitPath)
        {
            string commonDirPath = Path.Combine(gitPath, "commondir");
            if (!File.Exists(commonDirPath))
            {
                return gitPath;
            }

            string commonDir = File.ReadAllText(commonDirPath).Trim();
            if (string.IsNullOrWhiteSpace(commonDir))
            {
                return gitPath;
            }

            return Path.IsPathRooted(commonDir)
                ? commonDir
                : Path.GetFullPath(Path.Combine(gitPath, commonDir));
        }

        private static string ReadGitRefCommit(string gitPath, string refName)
        {
            if (string.IsNullOrWhiteSpace(gitPath))
            {
                return null;
            }

            string normalizedRefName = refName.Replace('/', Path.DirectorySeparatorChar);
            string refPath = Path.Combine(gitPath, normalizedRefName);
            if (File.Exists(refPath))
            {
                string commit = File.ReadAllText(refPath).Trim();
                if (!string.IsNullOrWhiteSpace(commit))
                {
                    return commit;
                }
            }

            string packedRefsPath = Path.Combine(gitPath, "packed-refs");
            if (!File.Exists(packedRefsPath))
            {
                return null;
            }

            string[] packedRefs = File.ReadAllLines(packedRefsPath);
            for (int index = 0; index < packedRefs.Length; index++)
            {
                string line = packedRefs[index];
                if (line.Length == 0 || line[0] == '#' || line[0] == '^')
                {
                    continue;
                }

                int separatorIndex = line.IndexOf(' ');
                if (
                    separatorIndex > 0
                    && string.Equals(
                        line.Substring(separatorIndex + 1),
                        refName,
                        StringComparison.Ordinal
                    )
                )
                {
                    return line.Substring(0, separatorIndex);
                }
            }

            return null;
        }

        private static string EscapeCsv(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
#endif
