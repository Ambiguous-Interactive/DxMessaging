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
    using global::Unity.Burst;
    using global::Unity.Collections;
    using global::Unity.Jobs;
    using NUnit.Framework;

#if UNITY_EDITOR
    [SetUpFixture]
    public sealed class NativeSdkFloorAdmission
    {
        [Serializable]
        private sealed class SourceFile
        {
            public string path;
            public string sha256;
        }

        [Serializable]
        private sealed class PackageEvidence
        {
            public string name;
            public string expectedVersion;
            public string actualVersion;
            public string source;
            public string resolvedPath;
            public SourceFile[] compilerSources;
        }

        [Serializable]
        private sealed class AdmissionEvidence
        {
            public int schemaVersion = 2;
            public string batchAssembly;
            public string batchPackage;
            public string batchVersion;
            public string unityVersion = UnityEngine.Application.unityVersion;
            public PackageEvidence[] packages;
        }

        [OneTimeSetUp]
        public void RequireActualFloorPackagesBeforeCandidates()
        {
            CaptureDirectCallEvidence("before", false);
            string output = Environment.GetEnvironmentVariable("DXM_NATIVE_SDK_FLOOR_EVIDENCE");
            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            string directory = System.IO.Path.GetDirectoryName(output);
            System.IO.Directory.CreateDirectory(directory);
            string project = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(UnityEngine.Application.dataPath, "..")
            );
            foreach (string name in new[] { "manifest.json", "packages-lock.json" })
            {
                System.IO.File.Copy(
                    System.IO.Path.Combine(project, "Packages", name),
                    System.IO.Path.Combine(directory, name),
                    false
                );
            }

            UnityEditor.PackageManager.PackageInfo[] registered =
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            KeyValuePair<string, string>[] expected =
            {
                new("com.unity.burst", "1.6.6"),
                new("com.unity.collections", "1.2.3"),
                new("com.unity.mathematics", "1.2.6"),
                new("com.unity.jobs", "0.50.0-preview.9"),
            };
            UnityEditor.PackageManager.PackageInfo batchPackage =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(IJobParallelForBatch).Assembly
                );
            AdmissionEvidence evidence = new()
            {
                packages = new PackageEvidence[expected.Length],
                batchAssembly = typeof(IJobParallelForBatch).Assembly.GetName().Name,
                batchPackage = batchPackage?.name ?? "missing",
                batchVersion = batchPackage?.version ?? "missing",
            };
            for (int index = 0; index < expected.Length; index++)
            {
                KeyValuePair<string, string> pin = expected[index];
                UnityEditor.PackageManager.PackageInfo package = Array.Find(
                    registered,
                    item => item.name == pin.Key
                );
                List<SourceFile> sources = new();
                if (package != null)
                {
                    string root = package.resolvedPath;
                    string[] paths = System.IO.Directory.GetFiles(
                        root,
                        "*",
                        System.IO.SearchOption.AllDirectories
                    );
                    Array.Sort(paths, StringComparer.Ordinal);
                    foreach (string path in paths)
                    {
                        if (
                            !path.EndsWith(".cs", StringComparison.Ordinal)
                            && !path.EndsWith(".asmdef", StringComparison.Ordinal)
                            && path != System.IO.Path.Combine(root, "package.json")
                        )
                        {
                            continue;
                        }
                        using System.Security.Cryptography.SHA256 hash =
                            System.Security.Cryptography.SHA256.Create();
                        using System.IO.FileStream stream = System.IO.File.OpenRead(path);
                        sources.Add(
                            new SourceFile
                            {
                                path = path.Substring(root.Length + 1).Replace('\\', '/'),
                                sha256 = BitConverter
                                    .ToString(hash.ComputeHash(stream))
                                    .Replace("-", "")
                                    .ToLowerInvariant(),
                            }
                        );
                    }
                }
                evidence.packages[index] = new PackageEvidence
                {
                    name = pin.Key,
                    expectedVersion = pin.Value,
                    actualVersion = package?.version ?? "",
                    source = package?.source.ToString() ?? "missing",
                    resolvedPath = package?.resolvedPath ?? "",
                    compilerSources = sources.ToArray(),
                };
            }
            System.IO.File.WriteAllText(output, UnityEngine.JsonUtility.ToJson(evidence, true));
            AssertActualFloor(evidence);
        }

        [Serializable]
        private sealed class DirectCallField
        {
            public string name;
            public string fieldType;
            public bool stateObserved;
            public bool nonZero;
        }

        [Serializable]
        private sealed class DirectCallMethod
        {
            public string declaringType;
            public string methodName;
            public string metadataToken;
            public string ilHex;
            public bool compileSynchronously;
            public bool tailCallMatched;
            public string tailCallTarget;
            public DirectCallType[] generatedTypes;
        }

        [Serializable]
        private sealed class DirectCallType
        {
            public string name;
            public DirectCallMethod[] methods;
            public DirectCallField[] fields;
        }

        [Serializable]
        private sealed class DirectCallEvidence
        {
            public int schemaVersion = 1;
            public string phase;
            public bool stateCaptured;
            public bool compilerEnabled;
            public bool optionEnabled;
            public bool optionSynchronous;
            public DirectCallMethod[] methods;
            public string[] errors;
        }

        [OneTimeTearDown]
        public void RetainDirectCallStateAfterCandidates()
        {
            CaptureDirectCallEvidence("after", true);
            CaptureCompilerTrace();
        }

        [Serializable]
        private sealed class CompilerLog
        {
            public string name;
            public string path;
            public string status;
            public long sourceLengthBytes;
            public int capturedBytes;
            public string sourceLastWriteUtc;
            public string sha256;
            public string content;
            public string error;
        }

        [Serializable]
        private sealed class RuntimeFile
        {
            public string name;
            public string path;
            public string sha256;
            public bool loadedAssemblyObserved;
            public string loadedAssemblyLocation;
            public string loadedAssemblySha256;
        }

        [Serializable]
        private sealed class CompilerTrace
        {
            public int schemaVersion = 1;
            public string capturedUtc;
            public string currentDirectory;
            public string runtimePath;
            public bool runtimeOverridePresent;
            public string debugLevel;
            public CompilerLog[] logs;
            public RuntimeFile[] runtimeFiles;
            public string[] errors;
        }

        private static CompilerLog ReadCompilerLog(string path, int maximumBytes)
        {
            CompilerLog record = new()
            {
                name = System.IO.Path.GetFileName(path),
                path = path,
                status = "missing",
            };
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    return record;
                }
                if (
                    (System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint)
                    != 0
                )
                {
                    throw new System.IO.IOException("Refusing a linked compiler log.");
                }
                DateTime before = System.IO.File.GetLastWriteTimeUtc(path);
                using System.IO.FileStream input = new(
                    path,
                    System.IO.FileMode.Open,
                    System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite,
                    4096,
                    System.IO.FileOptions.SequentialScan
                );
                record.sourceLengthBytes = input.Length;
                record.sourceLastWriteUtc = before.ToString("O");
                byte[] bytes = new byte[(int)Math.Min(record.sourceLengthBytes, maximumBytes)];
                while (record.capturedBytes < bytes.Length)
                {
                    int count = input.Read(
                        bytes,
                        record.capturedBytes,
                        bytes.Length - record.capturedBytes
                    );
                    if (count == 0)
                    {
                        throw new System.IO.EndOfStreamException(
                            "Compiler log shortened during capture."
                        );
                    }
                    record.capturedBytes += count;
                }
                using System.Security.Cryptography.SHA256 hash =
                    System.Security.Cryptography.SHA256.Create();
                record.sha256 = BitConverter
                    .ToString(hash.ComputeHash(bytes))
                    .Replace("-", "")
                    .ToLowerInvariant();
                record.content = new System.Text.UTF8Encoding(false, true).GetString(bytes);
                record.status = record.sourceLengthBytes <= maximumBytes ? "ok" : "truncated";
                if (
                    input.Length != record.sourceLengthBytes
                    || System.IO.File.GetLastWriteTimeUtc(path) != before
                )
                {
                    record.status = "changed";
                }
            }
            catch (Exception error)
            {
                record.status = "error";
                record.error = error.ToString();
            }
            return record;
        }

        private static string HashRuntimeFile(string path)
        {
            using System.Security.Cryptography.SHA256 hash =
                System.Security.Cryptography.SHA256.Create();
            using System.IO.FileStream input = System.IO.File.OpenRead(path);
            return BitConverter
                .ToString(hash.ComputeHash(input))
                .Replace("-", "")
                .ToLowerInvariant();
        }

        private static void CaptureCompilerTrace()
        {
            string output = Environment.GetEnvironmentVariable("DXM_NATIVE_BURST_TRACE_EVIDENCE");
            if (string.IsNullOrEmpty(output))
            {
                return;
            }
            output = System.IO.Path.GetFullPath(output);
            if (System.IO.File.Exists(output))
            {
                throw new System.IO.IOException("Refusing existing compiler trace: " + output);
            }
            List<string> errors = new();
            List<CompilerLog> logs = new();
            List<RuntimeFile> runtime = new();
            CompilerTrace trace = new()
            {
                capturedUtc = DateTime.UtcNow.ToString("O"),
                currentDirectory = Environment.CurrentDirectory,
                runtimeOverridePresent = !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable("UNITY_BURST_RUNTIME_PATH")
                ),
                debugLevel = Environment.GetEnvironmentVariable("UNITY_BURST_DEBUG"),
            };
            try
            {
                Type loader = typeof(BurstCompiler).Assembly.GetType(
                    "Unity.Burst.Editor.BurstLoader",
                    true
                );
                trace.runtimePath = (string)loader.GetProperty("RuntimePath").GetValue(null);
                foreach (string name in new[] { "Burst.Compiler.IL.dll", "Burst.Backend.dll" })
                {
                    RuntimeFile file = new()
                    {
                        name = name,
                        path = System.IO.Path.Combine(trace.runtimePath, name),
                    };
                    runtime.Add(file);
                    file.sha256 = HashRuntimeFile(file.path);
                    System.Reflection.Assembly loaded = AppDomain
                        .CurrentDomain.GetAssemblies()
                        .SingleOrDefault(assembly =>
                            assembly.GetName().Name
                            == System.IO.Path.GetFileNameWithoutExtension(name)
                        );
                    if (loaded != null)
                    {
                        file.loadedAssemblyObserved = true;
                        file.loadedAssemblyLocation = loaded.Location;
                        file.loadedAssemblySha256 = HashRuntimeFile(loaded.Location);
                    }
                }
            }
            catch (Exception error)
            {
                errors.Add("Runtime identity: " + error);
            }
            try
            {
                string directory = System.IO.Path.Combine(trace.currentDirectory, "Logs");
                if (
                    (
                        System.IO.File.GetAttributes(directory)
                        & System.IO.FileAttributes.ReparsePoint
                    ) != 0
                )
                {
                    throw new System.IO.IOException("Refusing a linked compiler log directory.");
                }
                string[] paths = System
                    .IO.Directory.GetFiles(directory, "burst*.log")
                    .Where(path =>
                        System.Text.RegularExpressions.Regex.IsMatch(
                            System.IO.Path.GetFileName(path),
                            @"^burst(?:-thread-\d+)?\.log$"
                        )
                    )
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                if (64 < paths.Length)
                {
                    errors.Add("Compiler trace has more than 64 fixed log files.");
                }
                long total = 0;
                foreach (string path in paths.Take(64))
                {
                    int remaining = (int)Math.Min(4 * 1024 * 1024, 64 * 1024 * 1024 - total);
                    if (remaining == 0)
                    {
                        errors.Add("Compiler trace exceeds the 64 MiB capture budget.");
                        break;
                    }
                    CompilerLog log = ReadCompilerLog(path, remaining);
                    logs.Add(log);
                    total += log.capturedBytes;
                    if (log.status != "ok" || log.capturedBytes == 0)
                    {
                        errors.Add(log.name + ": " + log.status);
                    }
                }
                if (
                    !logs.Any(log => log.name == "burst.log")
                    || !logs.Any(log =>
                        log.name.StartsWith("burst-thread-", StringComparison.Ordinal)
                    )
                )
                {
                    errors.Add("Compiler trace requires main and thread log observations.");
                }
            }
            catch (Exception error)
            {
                errors.Add("Compiler logs: " + error);
            }
            trace.logs = logs.ToArray();
            trace.runtimeFiles = runtime.ToArray();
            trace.errors = errors.ToArray();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output));
            System.IO.File.WriteAllText(output, UnityEngine.JsonUtility.ToJson(trace, true));
        }

        private static DirectCallMethod ReadDirectCallMethod(System.Reflection.MethodInfo method)
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
            int callOffset = il.Length - 6;
            bool tailCall = 0 <= callOffset && il[callOffset] == 0x28 && il[il.Length - 1] == 0x2a;
            System.Reflection.MethodBase target = tailCall
                ? method.Module.ResolveMethod(BitConverter.ToInt32(il, callOffset + 1))
                : null;
            BurstCompileAttribute attribute = method
                .GetCustomAttributes(typeof(BurstCompileAttribute), false)
                .Cast<BurstCompileAttribute>()
                .SingleOrDefault();
            return new DirectCallMethod
            {
                declaringType = method.DeclaringType.FullName,
                methodName = method.Name,
                metadataToken = method.MetadataToken.ToString("x8"),
                ilHex = BitConverter.ToString(il).Replace("-", "").ToLowerInvariant(),
                compileSynchronously = attribute?.CompileSynchronously ?? false,
                tailCallMatched = tailCall,
                tailCallTarget =
                    target == null ? "" : target.DeclaringType.FullName + "." + target.Name,
                generatedTypes = Array.Empty<DirectCallType>(),
            };
        }

        private static void CaptureDirectCallEvidence(string phase, bool captureState)
        {
            string prefix = Environment.GetEnvironmentVariable("DXM_NATIVE_DIRECTCALL_EVIDENCE");
            if (string.IsNullOrEmpty(prefix))
            {
                return;
            }
            string path = System.IO.Path.GetFullPath(prefix + "." + phase + ".json");
            if (System.IO.File.Exists(path))
            {
                throw new System.IO.IOException("Refusing existing direct-call evidence: " + path);
            }
            List<string> errors = new();
            List<DirectCallMethod> methods = new();
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (
                KeyValuePair<Type, string> wrapper in new[]
                {
                    new KeyValuePair<Type, string>(typeof(PureBatchKernels), "Direct"),
                    new KeyValuePair<Type, string>(typeof(VectorPureKernels), "ScalarDirect"),
                    new KeyValuePair<Type, string>(typeof(VectorPureKernels), "VectorDirect"),
                }
            )
            {
                try
                {
                    DirectCallMethod record = ReadDirectCallMethod(
                        wrapper.Key.GetMethod(wrapper.Value, flags)
                    );
                    List<DirectCallType> generated = new();
                    foreach (
                        Type type in wrapper
                            .Key.GetNestedTypes(
                                System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.NonPublic
                            )
                            .Where(type =>
                                type.Name.StartsWith(wrapper.Value, StringComparison.Ordinal)
                                && type.Name.EndsWith("$BurstDirectCall", StringComparison.Ordinal)
                            )
                    )
                    {
                        List<DirectCallField> fields = new();
                        foreach (System.Reflection.FieldInfo field in type.GetFields(flags))
                        {
                            bool observe = captureState && field.FieldType == typeof(IntPtr);
                            fields.Add(
                                new DirectCallField
                                {
                                    name = field.Name,
                                    fieldType = field.FieldType.FullName,
                                    stateObserved = observe,
                                    nonZero =
                                        observe && (IntPtr)field.GetValue(null) != IntPtr.Zero,
                                }
                            );
                        }
                        generated.Add(
                            new DirectCallType
                            {
                                name = type.FullName,
                                methods = type.GetMethods(flags)
                                    .Select(ReadDirectCallMethod)
                                    .ToArray(),
                                fields = fields.ToArray(),
                            }
                        );
                    }
                    record.generatedTypes = generated.ToArray();
                    methods.Add(record);
                }
                catch (Exception error)
                {
                    errors.Add(wrapper.Key.FullName + "." + wrapper.Value + ": " + error);
                }
            }
            DirectCallEvidence evidence = new()
            {
                phase = phase,
                stateCaptured = captureState,
                compilerEnabled = captureState && BurstCompiler.IsEnabled,
                optionEnabled = captureState && BurstCompiler.Options.EnableBurstCompilation,
                optionSynchronous =
                    captureState && BurstCompiler.Options.EnableBurstCompileSynchronously,
                methods = methods.ToArray(),
                errors = errors.ToArray(),
            };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, UnityEngine.JsonUtility.ToJson(evidence, true));
        }

        private static void AssertActualFloor(AdmissionEvidence evidence)
        {
            Assert.That(evidence.unityVersion, Is.EqualTo("2021.3.45f1"));
            Assert.That(evidence.batchAssembly, Is.EqualTo("Unity.Jobs"));
            Assert.That(evidence.batchPackage, Is.EqualTo("com.unity.jobs"));
            Assert.That(evidence.batchVersion, Is.EqualTo("0.50.0-preview.9"));
            foreach (PackageEvidence package in evidence.packages)
            {
                Assert.That(
                    package.actualVersion,
                    Is.EqualTo(package.expectedVersion),
                    package.name
                );
                Assert.That(package.source, Is.EqualTo("Registry"), package.name);
                Assert.That(package.compilerSources.Length, Is.GreaterThan(0), package.name);
                Assert.That(
                    package.compilerSources.Any(file => file.path == "package.json"),
                    Is.True,
                    package.name
                );
            }
        }
    }
#endif

    [Category("NativeSdkCpu")]
    public sealed class NativeCollectionsBaselineTests
    {
        private readonly List<NativeOwner> _owned = new();
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
                foreach (NativeOwner owner in _owned)
                {
                    try
                    {
                        owner.Dispose();
                        owner.Dispose();
                        Assert.That(owner.HasPending, Is.False);
                        Assert.That(owner.Queue.IsCreated, Is.False);
                        Assert.That(owner.Stream.IsCreated, Is.False);
                        Assert.That(owner.Modes.IsCreated, Is.False);
                        Assert.That(owner.WorkerIds.IsCreated, Is.False);
                        Assert.That(owner.ReleaseCount, Is.EqualTo(3));
                    }
                    catch (Exception failure)
                    {
                        failures.Add(failure);
                    }
                }
                TestContext.WriteLine(
                    $"Native cleanup: owners={_owned.Count}; three container owners released once each after job completion."
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
        public void QueueGrowsAndPreservesSingleProducerFifoAcrossClearAndReuse(
            [Values(1, 4096, 8192)] int items
        )
        {
            using NativeOwner owner = Create(false, 1);
            for (int repeat = 0; repeat < 3; ++repeat)
            {
                for (int sequence = 0; sequence < items; ++sequence)
                {
                    owner.Queue.Enqueue(NativeNumericPayload.Create(repeat, sequence));
                }
                Assert.That(owner.Queue.Count, Is.EqualTo(items));
                for (int sequence = 0; sequence < items; ++sequence)
                {
                    Assert.That(owner.Queue.TryDequeue(out NativeNumericPayload payload), Is.True);
                    AssertPayload(payload, repeat, sequence);
                }
                Assert.That(owner.Queue.IsEmpty(), Is.True);
                Assert.That(owner.Queue.TryDequeue(out _), Is.False);
                owner.Queue.Enqueue(NativeNumericPayload.Create(repeat, 999));
                owner.Queue.Clear();
                Assert.That(owner.Queue.Count, Is.Zero);
            }
        }

        [Test]
        public void QueueCopiesNumericValuesAtEnqueue()
        {
            using NativeOwner owner = Create(false, 1);
            NativeNumericPayload input = NativeNumericPayload.Create(2, 3);
            owner.Queue.Enqueue(input);
            input.Value = 99;
            input.Sequence = 99;
            AssertPayload(owner.Queue.Dequeue(), 2, 3);
            Assert.That(owner.Queue.Count, Is.Zero);
        }

        [Test]
        public void QueueConsumerItemBudgetLeavesCallbackEnqueueForNextPass()
        {
            using NativeOwner owner = Create(false, 1);
            for (int index = 0; index < 3; ++index)
            {
                owner.Queue.Enqueue(NativeNumericPayload.Create(0, index));
            }
            int budget = owner.Queue.Count;
            List<int> observed = new();
            for (int index = 0; index < budget; ++index)
            {
                NativeNumericPayload payload = owner.Queue.Dequeue();
                observed.Add(payload.Sequence);
                if (index == 0)
                {
                    owner.Queue.Enqueue(NativeNumericPayload.Create(0, 99));
                }
            }
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, observed);
            Assert.That(owner.Queue.Count, Is.EqualTo(1));
            AssertPayload(owner.Queue.Dequeue(), 0, 99);
        }

        [Test]
        public void QueueConsumerExceptionConsumesCurrentValueAndRetainsSuffix()
        {
            using NativeOwner owner = Create(false, 1);
            for (int index = 0; index < 3; ++index)
            {
                owner.Queue.Enqueue(NativeNumericPayload.Create(0, index));
            }
            InvalidOperationException expected = new("Expected consumer failure.");
            List<int> seen = new();
            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            {
                while (owner.Queue.TryDequeue(out NativeNumericPayload payload))
                {
                    seen.Add(payload.Sequence);
                    if (payload.Sequence == 1)
                    {
                        throw expected;
                    }
                }
            });
            Assert.That(actual, Is.SameAs(expected));
            CollectionAssert.AreEqual(new[] { 0, 1 }, seen);
            Assert.That(owner.Queue.Count, Is.EqualTo(1));
            AssertPayload(owner.Queue.Dequeue(), 0, 2);
        }

        [Test]
        public void QueueCopyRequiresLiveOwnerAndDisposedOwnerRejectsScheduling()
        {
            NativeOwner owner = Create(false, 1);
            NativeQueue<NativeNumericPayload> alias = owner.Queue;
            Assert.That(alias.IsCreated, Is.True);
            owner.Dispose();
            Assert.That(owner.Queue.IsCreated, Is.False);
            Assert.Throws<InvalidOperationException>(() => owner.Schedule(1));
            // Even IsCreated dereferences the queue pointer: no access to the stale copy.
        }

        [Test]
        public void StreamFixedPartitionReplayChangesCompletionOrderAndFreshReaderReplays()
        {
            using NativeOwner owner = Create(true, 2);
            NativeStream.Writer writer = owner.Stream.AsWriter();
            writer.BeginForEachIndex(1);
            writer.Write(NativeNumericPayload.Create(1, 2));
            writer.EndForEachIndex();
            writer.BeginForEachIndex(0);
            writer.Write(NativeNumericPayload.Create(0, 1));
            writer.Write(NativeNumericPayload.Create(0, 3));
            writer.EndForEachIndex();
            List<NativeNumericPayload> first = ReadPartitions(owner.Stream);
            List<NativeNumericPayload> second = ReadPartitions(owner.Stream);
            CollectionAssert.AreEqual(new[] { 1, 3, 2 }, first.Select(p => p.Sequence).ToArray());
            CollectionAssert.AreNotEqual(
                new[] { 2, 1, 3 },
                first.Select(p => p.Sequence).ToArray()
            );
            CollectionAssert.AreEqual(first, second);
            Assert.That(owner.Stream.Count(), Is.EqualTo(3));
            TestContext.WriteLine(
                "Stream RED: completion B2,A1,A3 -> partition replay A1,A3,B2; a new reader repeats all three."
            );
        }

        [Test]
        public void StreamExplicitClosePublishesWrittenPrefixAfterManagedProducerFailure()
        {
            using NativeOwner owner = Create(true, 1);
            NativeStream.Writer writer = owner.Stream.AsWriter();
            InvalidOperationException expected = new("Expected producer failure.");
            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            {
                writer.BeginForEachIndex(0);
                try
                {
                    writer.Write(NativeNumericPayload.Create(0, 0));
                    writer.Write(NativeNumericPayload.Create(0, 1));
                    throw expected;
                }
                finally
                {
                    writer.EndForEachIndex();
                }
            });
            Assert.That(actual, Is.SameAs(expected));
            CollectionAssert.AreEqual(
                new[] { 0, 1 },
                ReadPartitions(owner.Stream).Select(p => p.Sequence)
            );
        }

        [Test]
        public void StreamResetIsQuiescentDisposalAndReconstruction()
        {
            NativeOwner prior = Create(true, 1);
            NativeStream.Writer writer = prior.Stream.AsWriter();
            writer.BeginForEachIndex(0);
            writer.Write(NativeNumericPayload.Create(0, 99));
            writer.EndForEachIndex();
            prior.Dispose();
            using NativeOwner next = Create(true, 1);
            Assert.That(next.Stream.Count(), Is.Zero);
            writer = next.Stream.AsWriter();
            writer.BeginForEachIndex(0);
            writer.Write(NativeNumericPayload.Create(0, 2));
            writer.EndForEachIndex();
            AssertPayload(ReadPartitions(next.Stream).Single(), 0, 2);
        }

        [Test]
        public void StreamCopiedIsCreatedFlagDoesNotRepresentLiveOwnership()
        {
            NativeOwner owner = Create(true, 1);
            NativeStream alias = owner.Stream;
            owner.Dispose();
            Assert.That(owner.Stream.IsCreated, Is.False);
            Assert.That(alias.IsCreated, Is.True);
            // No reader, writer, count or disposal access through the stale copy.
        }

        [Test]
        public void BurstProducerJobsPublishExactSetAndPerProducerOrder(
            [Values(false, true)] bool stream,
            [Values(1, 4, 32)] int producers,
            [Values(1, 1024)] int items
        )
        {
            using NativeOwner owner = Create(stream, producers);
            owner.Schedule(items);
            owner.Complete();
            AssertBurst(owner);
            List<NativeNumericPayload> actual = stream
                ? ReadPartitions(owner.Stream)
                : ReadQueue(owner);
            Assert.That(actual.Count, Is.EqualTo(producers * items));
            HashSet<long> membership = new();
            int[] next = new int[producers];
            long checksum = 0;
            foreach (NativeNumericPayload payload in actual)
            {
                Assert.That(payload.Producer, Is.InRange(0, producers - 1));
                AssertPayload(payload, payload.Producer, next[payload.Producer]++);
                Assert.That(membership.Add(payload.Value), Is.True, "No duplicates.");
                checksum = checked(checksum + payload.Value);
            }
            foreach (int count in next)
            {
                Assert.That(count, Is.EqualTo(items));
            }
            long expected = checked(
                (long)items * producers * (producers - 1) / 2 * (1L << 32)
                + (long)producers * items * (items - 1) / 2
            );
            Assert.That(checksum, Is.EqualTo(expected));
            if (stream)
            {
                for (int index = 0; index < actual.Count; ++index)
                {
                    AssertPayload(actual[index], index / items, index % items);
                }
            }
            TestContext.WriteLine(
                $"Burst baseline: stream={stream},producers={producers},items={items},checksum={checksum},workers=[{string.Join(",", owner.WorkerIds.ToArray().Distinct().OrderBy(id => id))}],modes=[{string.Join(",", owner.Modes.ToArray())}]."
            );
        }

        [Test]
        public void BurstDiscardManagedControlSetsMarker()
        {
            int marker = 0;
            NativeProducerMode.MarkManaged(ref marker);
            Assert.That(marker, Is.EqualTo(1));
        }

        [Test]
        public void DisposeCompletesOutstandingProducerBeforeReleasingUniqueOwners(
            [Values(false, true)] bool stream,
            [Values(1, 4096)] int items
        )
        {
            NativeOwner owner = Create(stream, 4);
            owner.Schedule(items);
            owner.Dispose();
            Assert.That(owner.CompletedScheduledJob, Is.True);
            Assert.That(owner.CompletedInBurst, Is.True);
            Assert.That(owner.HasPending, Is.False);
            Assert.That(owner.ReleaseCount, Is.EqualTo(3));
        }

        [Test]
        public void CompletedBurstProducerReplaysPublicBusOnMainThread(
            [Values(false, true)] bool stream,
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
                label: $"Native SDK public replay {scenario.Kind}/{boundary}"
            );
            try
            {
                MessageHandler handler = new(new InstanceId(505_379), bus) { active = true };
                using MessageRegistrationToken token = MessageRegistrationToken.Create(
                    handler,
                    bus
                );
                token.DiagnosticMode = false;
                token.Enable();
                List<int> seen = new();
                int multiplier = 1;
                InvalidOperationException expected = new("Expected public consumer failure.");
                void OnPayload(in NativeNumericPayload payload)
                {
                    Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(mainThread));
                    AssertPayload(payload, 0, payload.Sequence);
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
                using NativeOwner owner = Create(stream, 1);
                owner.Schedule(3);
                owner.Complete();
                AssertBurst(owner);
                NativeStream.Reader reader = default;
                if (stream)
                {
                    reader = owner.Stream.AsReader();
                    Assert.That(reader.BeginForEachIndex(0), Is.EqualTo(3));
                }
                for (int index = 0; index < 3; ++index)
                {
                    NativeNumericPayload payload = stream
                        ? reader.Read<NativeNumericPayload>()
                        : owner.Queue.Dequeue();
                    if (index == 1 && boundary == "Throw")
                    {
                        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
                            () =>
                                Dispatch(payload)
                        );
                        Assert.That(actual, Is.SameAs(expected));
                        CollectionAssert.AreEqual(new[] { 0, 1 }, seen);
                        Assert.That(
                            stream ? reader.RemainingItemCount : owner.Queue.Count,
                            Is.EqualTo(1)
                        );
                    }
                    else
                    {
                        Dispatch(payload);
                    }
                }
                if (stream)
                {
                    reader.EndForEachIndex();
                    Assert.That(
                        owner.Stream.Count(),
                        Is.EqualTo(3),
                        "Reader cursor consumption leaves backing data for replay."
                    );
                }
                else
                {
                    Assert.That(owner.Queue.Count, Is.Zero);
                }
                CollectionAssert.AreEqual(
                    boundary == "Mutate" ? new[] { 0, 1, 20 } : new[] { 0, 1, 2 },
                    seen
                );
            }
            finally
            {
                bus.Trim(force: true);
            }
        }

        private NativeOwner Create(bool stream, int producers)
        {
            NativeOwner owner = new(stream, producers);
            _owned.Add(owner);
            return owner;
        }

        private static void AssertPayload(NativeNumericPayload payload, int producer, int sequence)
        {
            Assert.That(payload.Producer, Is.EqualTo(producer));
            Assert.That(payload.Sequence, Is.EqualTo(sequence));
            Assert.That(payload.Value, Is.EqualTo(((long)producer << 32) | (uint)sequence));
        }

        private static void AssertBurst(NativeOwner owner)
        {
            Assert.That(BurstCompiler.IsEnabled, Is.True, "Prerecorded current host capability.");
            foreach (int mode in owner.Modes)
            {
                Assert.That(mode, Is.Zero, "Reject managed fallback.");
            }
        }

        private static List<NativeNumericPayload> ReadPartitions(NativeStream stream)
        {
            List<NativeNumericPayload> result = new();
            NativeStream.Reader reader = stream.AsReader();
            for (int partition = 0; partition < stream.ForEachCount; ++partition)
            {
                int count = reader.BeginForEachIndex(partition);
                for (int index = 0; index < count; ++index)
                {
                    NativeNumericPayload copied = reader.Read<NativeNumericPayload>();
                    result.Add(copied);
                }
                reader.EndForEachIndex();
            }
            return result;
        }

        private static List<NativeNumericPayload> ReadQueue(NativeOwner owner)
        {
            List<NativeNumericPayload> result = new();
            while (owner.Queue.TryDequeue(out NativeNumericPayload payload))
            {
                result.Add(payload);
            }
            return result;
        }

        // Test ownership only: these mutable native structs have one disposing reference owner.
        private sealed class NativeOwner : IDisposable
        {
            internal NativeQueue<NativeNumericPayload> Queue;
            internal NativeStream Stream;
            internal NativeArray<int> Modes;
            internal NativeArray<int> WorkerIds;
            internal bool HasPending;
            internal bool CompletedScheduledJob;
            internal bool CompletedInBurst;
            internal int ReleaseCount;
            private readonly bool _stream;
            private readonly int _producers;
            private JobHandle _pending;
            private bool _disposed;

            internal NativeOwner(bool stream, int producers)
            {
                _stream = stream;
                _producers = producers;
                try
                {
                    if (stream)
                    {
                        Stream = new NativeStream(producers, Allocator.Persistent);
                    }
                    else
                    {
                        Queue = new NativeQueue<NativeNumericPayload>(Allocator.Persistent);
                    }
                    Modes = new NativeArray<int>(producers, Allocator.Persistent);
                    WorkerIds = new NativeArray<int>(producers, Allocator.Persistent);
                    for (int index = 0; index < producers; ++index)
                    {
                        Modes[index] = -1;
                        WorkerIds[index] = -1;
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal void Schedule(int items)
            {
                if (_disposed || HasPending || CompletedScheduledJob)
                {
                    throw new InvalidOperationException(
                        "Test owner permits one live producer schedule."
                    );
                }
                _pending = _stream
                    ? new StreamProducerJob
                    {
                        Writer = Stream.AsWriter(),
                        Modes = Modes,
                        WorkerIds = WorkerIds,
                        Items = items,
                    }.Schedule(_producers, 1)
                    : new QueueProducerJob
                    {
                        Writer = Queue.AsParallelWriter(),
                        Modes = Modes,
                        WorkerIds = WorkerIds,
                        Items = items,
                    }.Schedule(_producers, 1);
                HasPending = true;
            }

            internal void Complete()
            {
                if (!HasPending)
                {
                    return;
                }
                _pending.Complete();
                HasPending = false;
                CompletedScheduledJob = true;
                CompletedInBurst = true;
                foreach (int mode in Modes)
                {
                    CompletedInBurst &= mode == 0;
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                Complete();
                if (Queue.IsCreated)
                {
                    Queue.Dispose();
                    ++ReleaseCount;
                }
                if (Stream.IsCreated)
                {
                    Stream.Dispose();
                    ++ReleaseCount;
                }
                if (Modes.IsCreated)
                {
                    Modes.Dispose();
                    ++ReleaseCount;
                }
                if (WorkerIds.IsCreated)
                {
                    WorkerIds.Dispose();
                    ++ReleaseCount;
                }
                _disposed = true;
            }
        }
    }
}
#endif
