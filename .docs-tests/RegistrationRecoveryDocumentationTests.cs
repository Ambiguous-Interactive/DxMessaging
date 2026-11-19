using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

[TestFixture]
internal sealed class RegistrationRecoveryDocumentationTests
{
    private byte[] _runtimeImage = Array.Empty<byte>();
    private byte[] _probeImage = Array.Empty<byte>();

    [OneTimeSetUp]
    public void CompileActualDocumentedOwnerAndRuntime()
    {
        string root = DocsSnippetCompilationTests.ResolveRepoRoot();
        string path = Path.Combine(root, "docs", "advanced", "registration-builders.md");
        string example = DocsSnippetCompilationTests
            .ExtractCodeBlocks(path, "csharp")
            .Single(code =>
                code.Contains("public sealed class RegistrationOwner", StringComparison.Ordinal)
            );
        _runtimeImage = RuntimeHashCompilationTests.CompilePortableRuntimeImage(false);
        string helperRoot = Path.Combine(root, "Tests", "Runtime", "TestUtilities");
        CSharpParseOptions options = new(LanguageVersion.CSharp9);
        List<SyntaxTree> trees = new()
        {
            CSharpSyntaxTree.ParseText(example, options, path),
            CSharpSyntaxTree.ParseText(ProbeSource, options, "RegistrationRecoveryProbe.cs"),
        };
        foreach (
            string helper in new[]
            {
                "DelegatingMessageBus.cs",
                "LeakWatcher.cs",
                "EntryStorageInspection.cs",
                "MessageBusInspection.cs",
            }
        )
        {
            string helperPath = Path.Combine(helperRoot, helper);
            trees.Add(
                CSharpSyntaxTree.ParseText(File.ReadAllText(helperPath), options, helperPath)
            );
        }
        string references = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "WallstopStudios.DxMessaging.Tests.Runtime",
            trees,
            references
                .Split(Path.PathSeparator)
                .Select(file => MetadataReference.CreateFromFile(file))
                .Append(MetadataReference.CreateFromImage(_runtimeImage)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
        Assert.That(
            compilation.GetDiagnostics(),
            Is.Empty,
            "Compile actual documented source and production helpers without tolerated diagnostics."
        );
        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(stream);
        Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));
        _probeImage = stream.ToArray();
    }

    [TestCaseSource(nameof(Cases))]
    public void DocumentedOwnerRetainsRecoveryAndDrainsRegistrations(string scenario)
    {
        AssemblyLoadContext context = new("RegistrationRecoveryGuide", isCollectible: true);
        try
        {
            context.LoadFromStream(new MemoryStream(_runtimeImage));
            Assembly probe = context.LoadFromStream(new MemoryStream(_probeImage));
            string[]? errors =
                probe
                    .GetType("RegistrationRecoveryExample.RecoveryProbe", throwOnError: true)!
                    .GetMethod("Run")!
                    .Invoke(null, new object[] { scenario }) as string[];
            Assert.That(errors, Is.Not.Null, "Actual guide caller: " + scenario);
            Assert.That(
                errors,
                Is.Empty,
                "Actual guide ownership: " + scenario + "\n" + string.Join("\n", errors!)
            );
        }
        finally
        {
            context.Unload();
        }
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (
            string scenario in new[]
            {
                "NormalInactive",
                "NormalActive",
                "LaterActivateDispose",
                "LaterActivateBuild",
                "LaterActivateActivate",
                "LaterActivateDeactivate",
                "LaterDeactivateDispose",
                "SetupConfigureClean",
                "SetupConfigureBlocked",
                "SetupOnBuildClean",
                "SetupOnBuildBlocked",
                "SetupOnActivateClean",
                "SetupOnActivateBlocked",
                "DisposeHookThrows",
                "DisposeBusThrows",
                "DisposeLateCleanup",
                "NestedDisposeClean",
                "NestedDisposeBlocked",
                "ReentrantBuildDuringBuild",
                "ReentrantDisposeDuringBuild",
                "ReentrantBuildDuringDispose",
                "ReplacementWhileActive",
                "ReplacementWhileRecovery",
            }
        )
        {
            yield return new TestCaseData(scenario).SetName(
                $"DocumentedOwnerRetainsRecoveryAndDrainsRegistrations({scenario})"
            );
        }
    }

    private const string ProbeSource = """
namespace RegistrationRecoveryExample
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;

    public static class RecoveryProbe
    {
        private static int _calls;

        public static string[] Run(string scenario)
        {
            if (scenario.StartsWith("Later", StringComparison.Ordinal)) { return RunLaterOperation(scenario); }
            _calls = 0;
            List<string> errors = new List<string>();
            MessageBus inner = new MessageBus();
            inner.DiagnosticsMode = false;
            using LeakWatcher leaks = new LeakWatcher(inner);
            FaultBus bus = new FaultBus(inner);
            MessageRegistrationBuilder builder = new MessageRegistrationBuilder();
            RegistrationOwner owner = new RegistrationOwner();
            InvalidOperationException setupFailure = new InvalidOperationException("guide setup marker");
            InvalidOperationException hookFailure = new InvalidOperationException("guide hook marker");
            MessageRegistrationBuildException recovery = null;
            MessageRegistrationLease cleanupLease = null;
            int disposeHooks = 0;
            Exception callbackFailure = null;
            bool setup = scenario.StartsWith("Setup", StringComparison.Ordinal) || scenario == "ReplacementWhileRecovery";
            bool blocked = scenario.EndsWith("Blocked", StringComparison.Ordinal) || scenario == "ReplacementWhileRecovery";
            string phase = scenario == "ReplacementWhileRecovery" ? "OnActivate"
                : scenario.Contains("Configure", StringComparison.Ordinal) ? "Configure"
                : scenario.Contains("OnBuild", StringComparison.Ordinal) ? "OnBuild" : "OnActivate";
            Action guardedCallback = () =>
            {
                try
                {
                    if (scenario == "ReentrantDisposeDuringBuild") { owner.Dispose(); }
                    else { owner.Build(builder, new MessageRegistrationBuildOptions()); }
                }
                catch (Exception failure)
                {
                    callbackFailure = failure;
                    throw;
                }
            };
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
            {
                Owner = new InstanceId(1951),
                PreferredMessageBus = bus,
                ActivateOnBuild = scenario != "NormalInactive",
                Configure = token =>
                {
                    token.DiagnosticMode = false;
                    token.RegisterUntargeted<Ping>(Handle);
                    if (setup && (phase == "Configure" || phase == "OnBuild"))
                    {
                        token.Enable();
                    }
                    if (setup && phase == "Configure") { throw setupFailure; }
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    onBuild: token =>
                    {
                        if (setup && phase == "OnBuild") { throw setupFailure; }
                        if (scenario == "ReentrantBuildDuringBuild" || scenario == "ReentrantDisposeDuringBuild")
                        {
                            guardedCallback();
                        }
                    },
                    onActivate: token =>
                    {
                        if (setup && phase == "OnActivate") { throw setupFailure; }
                    },
                    onDeactivate: null,
                    onDispose: token =>
                    {
                        ++disposeHooks;
                        if (scenario == "DisposeHookThrows") { throw hookFailure; }
                        if (scenario == "ReentrantBuildDuringDispose") { guardedCallback(); }
                        if (scenario == "DisposeLateCleanup")
                        {
                            token.Enable();
                            bus.OnCleanup = () => token.RegisterUntargeted<Ping>(Handle, priority: 1);
                        }
                    }
                )
            };
            if (setup && blocked) { bus.CleanupFailures = 2; }
            Exception buildFailure = null;
            try
            {
                try { owner.Build(builder, options); }
                catch (Exception failure)
                {
                    buildFailure = failure;
                    recovery = failure as MessageRegistrationBuildException;
                }
                cleanupLease = owner.Lease ?? recovery?.Lease;
                if (setup)
                {
                    if (blocked)
                    {
                        Check(recovery != null, "Blocked setup must expose recovery", errors);
                        Check(owner.Lease != null && ReferenceEquals(owner.Lease, recovery?.Lease), "Recovery owner must retain the exact lease before rethrow", errors);
                        Check(ReferenceEquals(recovery?.ActivationException, setupFailure), "Original setup failure reference", errors);
                        Check(ReferenceEquals(recovery?.CleanupException, bus.CleanupFailure), "Original cleanup failure reference", errors);
                        Check(recovery?.Lease.Token.Enabled == true && inner.RegisteredUntargeted == 1, "Failed cleanup remains live", errors);
                    }
                    else
                    {
                        Check(ReferenceEquals(buildFailure, setupFailure), "Clean rollback preserves setup exception", errors);
                        Check(owner.Lease == null && inner.RegisteredUntargeted == 0, "Clean rollback has no owned live lease", errors);
                    }
                    if (scenario == "ReplacementWhileRecovery")
                    {
                        MessageRegistrationLease retained = owner.Lease;
                        CheckRejected(() => owner.Build(builder, new MessageRegistrationBuildOptions()), errors);
                        Check(ReferenceEquals(retained, owner.Lease), "Rejected replacement preserves recovery owner", errors);
                    }
                }
                else if (scenario == "ReentrantBuildDuringBuild" || scenario == "ReentrantDisposeDuringBuild")
                {
                    Check(callbackFailure is InvalidOperationException && ReferenceEquals(buildFailure, callbackFailure), "Build-time owner reentry is rejected without replacing its failure", errors);
                    Check(owner.Lease == null && inner.RegisteredUntargeted == 0, "Rejected build-time reentry cleans its token", errors);
                }
                else
                {
                    Check(buildFailure == null && owner.Lease != null, "Build creates the owning lease", errors);
                    if (scenario == "NormalInactive")
                    {
                        Check(!owner.Lease.Token.Enabled && inner.RegisteredUntargeted == 0, "Inactive build stages without activating", errors);
                        owner.Activate();
                    }
                    Check(inner.RegisteredUntargeted == 1, "One actual subscription is active", errors);
                    if (scenario == "ReplacementWhileActive")
                    {
                        MessageRegistrationLease retained = owner.Lease;
                        CheckRejected(() => owner.Build(builder, new MessageRegistrationBuildOptions()), errors);
                        Check(ReferenceEquals(retained, owner.Lease) && inner.RegisteredUntargeted == 1, "Rejected replacement preserves the original subscription", errors);
                    }
                    if (scenario == "NestedDisposeClean" || scenario == "NestedDisposeBlocked")
                    {
                        bus.OnCleanup = owner.Dispose;
                    }
                    if (scenario == "DisposeBusThrows" || scenario == "NestedDisposeBlocked")
                    {
                        bus.CleanupFailures = 2;
                    }
                    Exception disposeFailure = null;
                    try { owner.Dispose(); }
                    catch (Exception failure) { disposeFailure = failure; }
                    if (scenario == "DisposeHookThrows")
                    {
                        Check(ReferenceEquals(disposeFailure, hookFailure), "Dispose hook exception identity", errors);
                        Check(owner.Lease != null && !owner.Lease.Token.Enabled, "Throwing cleanup keeps the owner even after token cleanup", errors);
                    }
                    else if (scenario == "DisposeBusThrows" || scenario == "NestedDisposeBlocked")
                    {
                        Check(ReferenceEquals(disposeFailure, bus.CleanupFailure), "Bus cleanup exception identity", errors);
                        Check(owner.Lease != null && owner.Lease.Token.Enabled && inner.RegisteredUntargeted == 1, "Rejected bus cleanup keeps a live owner", errors);
                    }
                    else if (scenario == "DisposeLateCleanup")
                    {
                        Check(disposeFailure == null && owner.Lease != null && owner.Lease.Token.Enabled && inner.RegisteredUntargeted == 1, "Returned disposal retains callback-accepted cleanup", errors);
                    }
                    else if (scenario == "ReentrantBuildDuringDispose")
                    {
                        Check(callbackFailure is InvalidOperationException && ReferenceEquals(disposeFailure, callbackFailure), "Disposal-time build reentry is rejected with its exception preserved", errors);
                        Check(owner.Lease != null && !owner.Lease.Token.Enabled, "Disposal exception keeps the owner for explicit retry", errors);
                    }
                    else
                    {
                        Check(disposeFailure == null && owner.Lease == null && inner.RegisteredUntargeted == 0, "Completed disposal releases its owner", errors);
                    }
                }
                bus.CleanupFailures = 0;
                bus.OnCleanup = null;
                owner.Dispose();
                Check(owner.Lease == null, "Explicit retry releases the cleanup owner", errors);
                Check(inner.RegisteredUntargeted == 0, "Actual live registrations are drained", errors);
                Check(disposeHooks == 1, "The disposal hook runs once across retries and reentry", errors);
                Ping ping = new Ping(1);
                inner.UntargetedBroadcast(ref ping);
                Check(_calls == 0, "Released subscriptions cannot receive another message", errors);
                return errors.ToArray();
            }
            finally
            {
                bus.CleanupFailures = 0;
                bus.OnCleanup = null;
                try { owner.Dispose(); }
                finally { cleanupLease?.Dispose(); }
            }
        }

        private static string[] RunLaterOperation(string scenario)
        {
            List<string> errors = new List<string>();
            MessageBus inner = new MessageBus();
            inner.DiagnosticsMode = false;
            using LeakWatcher leaks = new LeakWatcher(inner);
            FaultBus bus = new FaultBus(inner);
            RegistrationOwner owner = new RegistrationOwner();
            MessageRegistrationBuilder builder = new MessageRegistrationBuilder();
            owner.Build(builder, new MessageRegistrationBuildOptions
            {
                Owner = new InstanceId(1952),
                PreferredMessageBus = bus,
                ActivateOnBuild = false,
                Configure = token => { token.DiagnosticMode = false; token.RegisterUntargeted<Ping>(Handle); }
            });
            MessageRegistrationLease retained = owner.Lease;
            bool deactivating = scenario == "LaterDeactivateDispose";
            Exception callbackFailure = null;
            Action callback = () =>
            {
                try
                {
                    if (scenario == "LaterActivateBuild") { owner.Build(builder, new MessageRegistrationBuildOptions()); }
                    else if (scenario == "LaterActivateActivate") { owner.Activate(); }
                    else if (scenario == "LaterActivateDeactivate") { owner.Deactivate(); }
                    else { owner.Dispose(); }
                }
                catch (Exception failure) { callbackFailure = failure; throw; }
            };
            try
            {
                if (deactivating) { owner.Activate(); bus.OnCleanup = callback; }
                else { bus.OnRegister = callback; }
                Exception observed = null;
                try
                {
                    if (deactivating) { owner.Deactivate(); }
                    else { owner.Activate(); }
                }
                catch (Exception failure) { observed = failure; }
                Check(callbackFailure is InvalidOperationException && ReferenceEquals(observed, callbackFailure), "Lifecycle reentry preserves the exact rejected-callback exception", errors);
                Check(ReferenceEquals(owner.Lease, retained), "Later operation retains the exact lease after rejection", errors);
                Check(retained.Token.Enabled == deactivating && inner.RegisteredUntargeted == (deactivating ? 1 : 0), "Rejected registration accepts no subscription; rejected cleanup remains live", errors);
                bus.OnRegister = null;
                bus.OnCleanup = null;
                owner.Activate();
                Check(retained.Token.Enabled && inner.RegisteredUntargeted == 1, "Owner activation recovers the existing subscription", errors);
                owner.Deactivate();
                Check(ReferenceEquals(owner.Lease, retained) && !retained.Token.Enabled && inner.RegisteredUntargeted == 0, "Owner deactivation retains a clean inactive lease", errors);
                owner.Activate();
                Check(retained.Token.Enabled && inner.RegisteredUntargeted == 1, "Owner can activate again after deactivation", errors);
                owner.Dispose();
                Check(owner.Lease == null && inner.RegisteredUntargeted == 0, "Owner disposal drains the later lifecycle subscription", errors);
                return errors.ToArray();
            }
            finally
            {
                bus.OnRegister = null;
                bus.OnCleanup = null;
                try { owner.Dispose(); }
                finally { retained.Dispose(); }
            }
        }

        private static void CheckRejected(Action action, List<string> errors)
        {
            try { action(); errors.Add("An owned operation or lease must reject replacement"); }
            catch (InvalidOperationException) { }
        }

        private static void Check(bool condition, string message, List<string> errors)
        {
            if (!condition) { errors.Add(message); }
        }

        private static void Handle(Ping message) { _calls += message.Value; }

        private readonly struct Ping : IUntargetedMessage
        {
            internal readonly int Value;
            internal Ping(int value) { Value = value; }
            public Type MessageType => typeof(Ping);
        }

        private sealed class FaultBus : DelegatingMessageBus
        {
            internal readonly InvalidOperationException CleanupFailure = new InvalidOperationException("guide cleanup marker");
            internal int CleanupFailures;
            internal Action OnCleanup;
            internal Action OnRegister;
            internal FaultBus(IMessageBus inner) : base(inner) { }
            public override MessageBusRegistration RegisterUntargeted<T>(MessageHandler messageHandler, int priority = 0)
            {
                Action callback = OnRegister;
                OnRegister = null;
                callback?.Invoke();
                return base.RegisterUntargeted<T>(messageHandler, priority);
            }
            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Action callback = OnCleanup;
                OnCleanup = null;
                callback?.Invoke();
                if (0 < CleanupFailures) { --CleanupFailures; throw CleanupFailure; }
                base.Deregister<T>(in registration);
            }
        }
    }
}

""";
}
