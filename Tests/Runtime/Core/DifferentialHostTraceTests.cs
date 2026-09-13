#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using UnityEngine.TestTools;

    /// <summary>Replays activity inputs and native scene transitions through production owner tokens.</summary>
    public sealed class DifferentialHostTraceTests : UnityFixtureBase
    {
        private static readonly bool[] PersistenceMutations = { false, true };
        private DiagnosticsScope _diagnostics;
        private Scene _ownedScene;
        private AsyncOperation _sceneUnload;

        [UnityTearDown]
        public IEnumerator UnloadOwnedScene()
        {
            if (_sceneUnload == null && _ownedScene.IsValid() && _ownedScene.isLoaded)
            {
                _sceneUnload = SceneManager.UnloadSceneAsync(_ownedScene);
            }
            if (_sceneUnload != null && !_sceneUnload.isDone)
            {
                yield return _sceneUnload;
            }
            Assert.That(
                !_ownedScene.IsValid() || !_ownedScene.isLoaded,
                Is.True,
                "Native differential replay must unload its owned scene during cleanup."
            );
            _ownedScene = default;
            _sceneUnload = null;
        }

        [UnityTest]
        [Category("UnityRuntime")]
        public IEnumerator SceneUnloadReplayPreservesPersistentOwnersAndDetectsLostPersistence(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [ValueSource(nameof(PersistenceMutations))] bool omitPersistence
        )
        {
            /*
                This native protocol has an explicit asynchronous boundary. It supplements
                version-seven generation; the managed generator does not claim scene coverage.
            */
            const int unloadOperation = 6;
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 1, priority: -1),
                    new BusTraceOperation(BusTraceOperationKind.Register, token: 2, priority: 1),
                    new BusTraceOperation(BusTraceOperationKind.SetDiagnostics, value: 1),
                    new BusTraceOperation(BusTraceOperationKind.SetDiagnostics, token: 1, value: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 11),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 13),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 17),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Disable),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 19),
                    new BusTraceOperation(BusTraceOperationKind.Enable),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 23),
                    new BusTraceOperation(BusTraceOperationKind.Remove),
                    new BusTraceOperation(BusTraceOperationKind.Trim, value: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 29),
                    new BusTraceOperation(BusTraceOperationKind.Disable),
                }
            );
            List<BusTraceObservation> control = new();
            List<BusTraceObservation> candidate = new();
            yield return ReplaySceneUnload(sequence, unloadOperation, false, control);
            yield return ReplaySceneUnload(sequence, unloadOperation, omitPersistence, candidate);
            string report =
                $"nativeProtocol=scene-unload-v1; unloadOperation={unloadOperation}; triggerToken=1; persistentToken=0; omitPersistence={omitPersistence}\n"
                + Report(sequence, control);
            Assert.That(control.All(item => item.Exception == null), Is.True, report);
            Assert.That(
                candidate.All(item => item.Exception == null),
                Is.True,
                report + "\nCandidate trace:\n" + Report(sequence, candidate)
            );
            foreach (int index in new[] { 5, 6 })
            {
                int value = sequence.Operations[index].Value;
                CollectionAssert.AreEqual(
                    new[]
                    {
                        $"token=1,value={value}",
                        $"token=0,value={value}",
                        $"token=2,value={value}",
                    },
                    control[index].Callbacks,
                    report
                );
            }
            foreach (int index in new[] { 7, 12 })
            {
                int value = sequence.Operations[index].Value;
                CollectionAssert.AreEqual(
                    new[] { $"token=0,value={value}", $"token=2,value={value}" },
                    control[index].Callbacks,
                    report
                );
            }
            foreach (int index in new[] { 10, 15 })
            {
                CollectionAssert.AreEqual(
                    new[] { $"token=2,value={sequence.Operations[index].Value}" },
                    control[index].Callbacks,
                    report
                );
            }
            StringAssert.Contains("host[1]=destroyed/0", control[7].State, report);
            StringAssert.Contains("enabled=1011", control[7].State, report);
            StringAssert.Contains(
                "tokenMetadataCallsHistory=1/3/3,0/0/0,1/0/0,0/0/0,",
                control[7].State,
                report
            );
            BusTraceMismatch mismatch = DifferentialBusTrace.Compare(control, candidate);
            if (omitPersistence)
            {
                Assert.That(mismatch, Is.Not.Null, report);
                report += "\n" + mismatch.BuildReport(sequence);
                Assert.That(mismatch.Index, Is.EqualTo(unloadOperation + 1), report);
                Assert.That(mismatch.Category, Is.EqualTo("callbacks"), report);
                CollectionAssert.AreEqual(
                    new[] { "token=2,value=17" },
                    candidate[7].Callbacks,
                    report
                );
            }
            else
            {
                Assert.That(mismatch, Is.Null, mismatch?.BuildReport(sequence) ?? report);
            }
        }

        private IEnumerator ReplaySceneUnload(
            BusTraceSequence sequence,
            int unloadOperation,
            bool omitPersistence,
            List<BusTraceObservation> observations
        )
        {
            Assert.That(
                DifferentialBusTrace.IsValid(sequence),
                Is.True,
                "Scene replay inputs must remain valid."
            );
            Scene originalScene = SceneManager.GetActiveScene();
            _sceneUnload = null;
            _ownedScene = SceneManager.CreateScene("DifferentialScene-" + sequence.Scenario.Kind);
            yield return null;
            bool requestUnload = false;
            using (
                NativeHostAdapter adapter = CreateAdapter(
                    sequence.Scenario,
                    configureHost: (slot, host) =>
                    {
                        if (slot < 2)
                        {
                            SceneManager.MoveGameObjectToScene(host, _ownedScene);
                            Assert.That(
                                host.scene,
                                Is.EqualTo(_ownedScene),
                                "The ordinary and persistent owners must start in the unloaded scene."
                            );
                            if (slot == 0 && !omitPersistence)
                            {
                                UnityEngine.Object.DontDestroyOnLoad(host);
                            }
                        }
                    },
                    onCallback: slot =>
                    {
                        if (requestUnload && slot == 1)
                        {
                            requestUnload = false;
                            _sceneUnload = SceneManager.UnloadSceneAsync(_ownedScene);
                        }
                    }
                )
            )
            {
                for (int index = 0; index < sequence.Operations.Count; ++index)
                {
                    requestUnload = index == unloadOperation;
                    observations.Add(adapter.Execute(sequence.Operations[index]));
                    if (index == unloadOperation)
                    {
                        Assert.That(
                            _sceneUnload,
                            Is.Not.Null,
                            "The registered callback must request scene unload."
                        );
                        yield return _sceneUnload;
                        Assert.That(
                            !_ownedScene.IsValid() || !_ownedScene.isLoaded,
                            Is.True,
                            "Native scene unload must finish before the next operation."
                        );
                        Assert.That(
                            SceneManager.GetActiveScene(),
                            Is.EqualTo(originalScene),
                            "Replay must preserve the runner's active scene."
                        );
                    }
                }
            }
        }

        [SetUp]
        public void SetUp() =>
            _diagnostics = new DiagnosticsScope(
                DiagnosticsTarget.Off,
                messageBufferSize: 16,
                diagnosticsStackTraces: false
            );

        [TearDown]
        public void RestoreDiagnostics() => _diagnostics.Dispose();

        [Test]
        public void NativeHostActivityPreservesOwnerTokensAndControlsCurrentDispatch(
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
                    new BusTraceOperation(BusTraceOperationKind.SetHandlerActive, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 11),
                    new BusTraceOperation(BusTraceOperationKind.Enable, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 13),
                    new BusTraceOperation(
                        BusTraceOperationKind.EmitWithHandlerActive,
                        value: 17,
                        handlerToken: 1,
                        handlerActive: true
                    ),
                    new BusTraceOperation(
                        BusTraceOperationKind.EmitWithHandlerActive,
                        value: 19,
                        handlerToken: 1
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Disable, token: 1),
                    new BusTraceOperation(
                        BusTraceOperationKind.SetHandlerActive,
                        token: 1,
                        handlerActive: true
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 23),
                    new BusTraceOperation(BusTraceOperationKind.Enable, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 29),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.SetHandlerActive, token: 1),
                    new BusTraceOperation(
                        BusTraceOperationKind.SetHandlerActive,
                        token: 1,
                        handlerActive: true
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 31),
                }
            );
            IReadOnlyList<BusTraceObservation> observations = Replay(sequence);
            string report = Report(sequence, observations);
            Assert.That(observations.All(item => item.Exception == null), Is.True, report);
            Assert.That(
                DifferentialBusTrace.Compare(observations, Replay(sequence)),
                Is.Null,
                report
            );
            foreach (int index in new[] { 3, 5, 7, 10, 16 })
            {
                CollectionAssert.AreEqual(
                    new[] { $"token=0,value={sequence.Operations[index].Value}" },
                    observations[index].Callbacks,
                    report
                );
            }
            foreach (int index in new[] { 6, 12 })
            {
                int value = sequence.Operations[index].Value;
                CollectionAssert.AreEqual(
                    new[] { $"token=0,value={value}", $"token=1,value={value}" },
                    observations[index].Callbacks,
                    report
                );
            }
            StringAssert.Contains("enabled=1111", observations[2].State, report);
            StringAssert.Contains("host[1]=0/0/0/1", observations[2].State, report);
            StringAssert.Contains("enabled=1011", observations[9].State, report);
            StringAssert.Contains("host[1]=1/1/1/1", observations[9].State, report);
            StringAssert.Contains("host[1]=1/1/1/1", observations[16].State, report);
        }

        [Test]
        public void InitiallyInactiveHostHonorsReceiveWhileDisabled(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool receiveWhileDisabled
        )
        {
            BusTraceSequence sequence = new(
                scenario,
                509,
                new[]
                {
                    new BusTraceOperation(BusTraceOperationKind.Register),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 11),
                    new BusTraceOperation(
                        BusTraceOperationKind.SetHandlerActive,
                        handlerActive: true
                    ),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 13),
                    new BusTraceOperation(BusTraceOperationKind.SetHandlerActive),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 17),
                }
            );
            IReadOnlyList<BusTraceObservation> observations = DifferentialBusTrace.Replay(
                sequence,
                kind =>
                    CreateAdapter(
                        kind,
                        startsActive: false,
                        receiveWhileDisabled: receiveWhileDisabled
                    )
            );
            string report =
                $"receiveWhileDisabled={receiveWhileDisabled}; " + Report(sequence, observations);
            Assert.That(observations.All(item => item.Exception == null), Is.True, report);
            foreach (int index in new[] { 1, 5 })
            {
                CollectionAssert.AreEqual(
                    receiveWhileDisabled
                        ? new[] { $"token=0,value={sequence.Operations[index].Value}" }
                        : Array.Empty<string>(),
                    observations[index].Callbacks,
                    report
                );
                StringAssert.Contains("host[0]=0/0/0/1", observations[index].State, report);
                StringAssert.Contains("enabled=1111", observations[index].State, report);
            }
            CollectionAssert.AreEqual(
                new[] { "token=0,value=13" },
                observations[3].Callbacks,
                report
            );
        }

        [Test]
        public void NativeHostDisableMutationIsDetectedAndShrunkWithCallbackDependencies(
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
                            value: 11,
                            handlerToken: 1
                        )
                        : new BusTraceOperation(BusTraceOperationKind.SetHandlerActive, token: 1),
                    new BusTraceOperation(BusTraceOperationKind.Emit, value: 13),
                    new BusTraceOperation(BusTraceOperationKind.Remove, token: 1),
                }
            );
            BusTraceMismatch mismatch = EvaluateMutation(sequence);
            Assert.That(mismatch, Is.Not.Null, $"[{scenario.Kind}] fromCallback={fromCallback}");
            string report = mismatch.BuildReport(sequence);
            Assert.That(mismatch.Category, Is.EqualTo("callbacks"), report);
            Assert.That(mismatch.Index, Is.EqualTo(fromCallback ? 3 : 4), report);
            Assert.That(mismatch.Control.State, Is.EqualTo(mismatch.Candidate.State), report);
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(sequence, EvaluateMutation);
            CollectionAssert.AreEqual(
                fromCallback
                    ? new[]
                    {
                        BusTraceOperationKind.Register,
                        BusTraceOperationKind.Register,
                        BusTraceOperationKind.EmitWithHandlerActive,
                    }
                    : new[]
                    {
                        BusTraceOperationKind.Register,
                        BusTraceOperationKind.SetHandlerActive,
                        BusTraceOperationKind.Emit,
                    },
                minimal.Operations.Select(operation => operation.Kind),
                report
            );
            Assert.That(DifferentialBusTrace.IsValid(minimal), Is.True, report);
            Assert.That(EvaluateMutation(minimal)?.Category, Is.EqualTo("callbacks"), report);
        }

        [UnityTest]
        [Category("UnityRuntime")]
        public IEnumerator GeneratedLifecycleReplayMatchesAndShrinksDestroyedHostMutation(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            _ownedScene = SceneManager.CreateScene("GeneratedDifferentialScene-" + scenario.Kind);
            yield return null;
            BusTraceSequence sequence = DifferentialBusTrace.Generate(
                scenario,
                17,
                32,
                BusTraceSequence.NativeLifecycleGeneratorVersion
            );
            IReadOnlyList<BusTraceObservation> control = Replay(sequence, _ownedScene);
            IReadOnlyList<BusTraceObservation> candidate = Replay(sequence, _ownedScene);
            BusTraceMismatch mismatch = DifferentialBusTrace.Compare(control, candidate);
            string report = mismatch?.BuildReport(sequence) ?? Report(sequence, control);
            Assert.That(mismatch, Is.Null, report);
            for (int index = 0; index < control.Count; ++index)
            {
                if (control[index].Exception != null)
                {
                    Assert.That(
                        sequence.Operations[index].Kind,
                        Is.EqualTo(BusTraceOperationKind.EmitWithThrow),
                        report
                    );
                    StringAssert.Contains(
                        "intentional trace callback failure",
                        control[index].Exception,
                        report
                    );
                }
            }

            BusTraceSequence lifecycleOnly = DifferentialBusTrace.Generate(
                scenario,
                509,
                6,
                BusTraceSequence.NativeLifecycleGeneratorVersion
            );
            BusTraceMismatch EvaluateDestroyMutation(BusTraceSequence input) =>
                DifferentialBusTrace.Compare(
                    Replay(input, _ownedScene),
                    Replay(input, _ownedScene, ignoreHostDestroy: true)
                );
            BusTraceMismatch destroyMismatch = EvaluateDestroyMutation(lifecycleOnly);
            Assert.That(
                destroyMismatch,
                Is.Not.Null,
                Report(lifecycleOnly, Replay(lifecycleOnly, _ownedScene))
            );
            Assert.That(
                destroyMismatch.Category,
                Is.EqualTo("state"),
                destroyMismatch.BuildReport(lifecycleOnly)
            );
            BusTraceSequence minimal = DifferentialBusTrace.Shrink(
                lifecycleOnly,
                EvaluateDestroyMutation
            );
            CollectionAssert.AreEqual(
                new[] { BusTraceOperationKind.DestroyHost },
                minimal.Operations.Select(operation => operation.Kind),
                destroyMismatch.BuildReport(lifecycleOnly)
            );
            Assert.That(
                DifferentialBusTrace.IsValid(minimal),
                Is.True,
                destroyMismatch.BuildReport(lifecycleOnly)
            );
            Assert.That(
                EvaluateDestroyMutation(minimal)?.Category,
                Is.EqualTo("state"),
                destroyMismatch.BuildReport(lifecycleOnly)
            );
        }

        private IReadOnlyList<BusTraceObservation> Replay(BusTraceSequence sequence) =>
            DifferentialBusTrace.Replay(sequence, kind => CreateAdapter(kind));

        private IReadOnlyList<BusTraceObservation> Replay(
            BusTraceSequence sequence,
            Scene lifecycleScene,
            bool ignoreHostDestroy = false
        ) =>
            DifferentialBusTrace.Replay(
                sequence,
                kind =>
                    CreateAdapter(
                        kind,
                        lifecycleScene: lifecycleScene,
                        ignoreHostDestroy: ignoreHostDestroy
                    )
            );

        private BusTraceMismatch EvaluateMutation(BusTraceSequence sequence) =>
            DifferentialBusTrace.Compare(
                Replay(sequence),
                DifferentialBusTrace.Replay(
                    sequence,
                    kind => CreateAdapter(kind, ignoreDisable: true)
                )
            );

        private NativeHostAdapter CreateAdapter(
            MessageScenario scenario,
            bool ignoreDisable = false,
            bool startsActive = true,
            bool receiveWhileDisabled = false,
            Action<int, GameObject> configureHost = null,
            Action<int> onCallback = null,
            Scene lifecycleScene = default,
            bool ignoreHostDestroy = false
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionTicks: 0,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            bus.DiagnosticsMode = false;
            NativeOwners owners = new(
                bus,
                slot =>
                {
                    GameObject host = Track(
                        new GameObject($"DifferentialHost-{scenario.Kind}-{slot}")
                    );
                    configureHost?.Invoke(slot, host);
                    return host;
                },
                startsActive,
                receiveWhileDisabled
            );
            return new NativeHostAdapter(
                scenario,
                bus,
                owners,
                ignoreDisable,
                onCallback,
                lifecycleScene,
                ignoreHostDestroy
            );
        }

        private static string Report(
            BusTraceSequence sequence,
            IReadOnlyList<BusTraceObservation> observations
        ) =>
            $"adapter=UnityHostSetActive; generator={sequence.Version}; seed={sequence.Seed}; kind={sequence.Scenario.Kind}\n"
            + string.Join(
                "\n",
                observations.Select(
                    (item, index) => $"[{index}] {sequence.Operations[index]}: {item}"
                )
            );

        private sealed class NativeOwners
        {
            internal readonly MessagingComponent[] Components = new MessagingComponent[
                BusTraceSequence.TokenCount
            ];

            internal NativeOwners(
                IMessageBus bus,
                Func<int, GameObject> createHost,
                bool startsActive,
                bool receiveWhileDisabled
            )
            {
                for (int slot = 0; slot < Components.Length; ++slot)
                {
                    GameObject host = createHost(slot);
                    host.SetActive(startsActive);
                    MessagingComponent owner = host.AddComponent<MessagingComponent>();
                    owner.emitMessagesWhenDisabled = receiveWhileDisabled;
                    owner.Configure(bus, MessageBusRebindMode.RebindActive);
                    Components[slot] = owner;
                }
            }

            internal MessageRegistrationToken CreateToken(int slot) =>
                Components[slot].Create(Components[slot]);
        }

        private sealed class NativeHostAdapter : MessageBusTraceAdapter
        {
            private readonly NativeOwners _owners;
            private readonly bool _ignoreDisable;
            private readonly Action<int> _onCallback;
            private readonly Scene _lifecycleScene;
            private readonly bool _ignoreHostDestroy;
            private string _lifecycleObservation = string.Empty;

            internal NativeHostAdapter(
                MessageScenario scenario,
                MessageBus bus,
                NativeOwners owners,
                bool ignoreDisable,
                Action<int> onCallback,
                Scene lifecycleScene,
                bool ignoreHostDestroy
            )
                : base(scenario, bus, reset: bus.ResetState, tokenFactory: owners.CreateToken)
            {
                _owners = owners;
                _ignoreDisable = ignoreDisable;
                _onCallback = onCallback;
                _lifecycleScene = lifecycleScene;
                _ignoreHostDestroy = ignoreHostDestroy;
            }

            protected override void OnCallback(int slot, IMessage message) =>
                _onCallback?.Invoke(slot);

            protected override void SetHandlerActive(int slot, bool active)
            {
                MessagingComponent owner = _owners.Components[slot];
                owner.gameObject.SetActive(active);
                if (_ignoreDisable && !active)
                {
                    /*
                        Mutate the production handler after the real native OnDisable callback.
                        Native host observations stay identical; only actual delivery reveals drift.
                    */
                    owner.ToggleMessageHandler(true);
                }
            }

            protected override void ExecuteHostLifecycle(BusTraceOperation operation)
            {
                MessagingComponent owner = _owners.Components[operation.Token];
                switch (operation.Kind)
                {
                    case BusTraceOperationKind.MoveHostToScene:
                        if (!_lifecycleScene.IsValid() || !_lifecycleScene.isLoaded)
                        {
                            throw new InvalidOperationException(
                                "Native lifecycle replay requires a loaded owned scene."
                            );
                        }
                        SceneManager.MoveGameObjectToScene(owner.gameObject, _lifecycleScene);
                        _lifecycleObservation =
                            $"move:{operation.Token}:{owner.gameObject.scene.name}";
                        break;
                    case BusTraceOperationKind.PersistHost:
                        UnityEngine.Object.DontDestroyOnLoad(owner.gameObject);
                        _lifecycleObservation =
                            $"persist:{operation.Token}:{owner.gameObject.scene.name}";
                        break;
                    case BusTraceOperationKind.DestroyHost:
                        if (!_ignoreHostDestroy)
                        {
                            UnityEngine.Object.DestroyImmediate(owner.gameObject);
                        }
                        _lifecycleObservation =
                            $"destroy:{operation.Token}:{(owner == null ? "destroyed" : "alive")}";
                        break;
                    default:
                        base.ExecuteHostLifecycle(operation);
                        break;
                }
            }

            // The owner hides its handler. Report observable native state below instead of inferring its flag.
            protected override string DescribeHandlerActivity() => "native-owner";

            protected override string DescribeAdapterState()
            {
                StringBuilder state = new(
                    "; adapter=UnityHostSetActive; lifecycle=" + _lifecycleObservation
                );
                for (int slot = 0; slot < _owners.Components.Length; ++slot)
                {
                    MessagingComponent owner = _owners.Components[slot];
                    if (owner == null)
                    {
                        state.Append(
                            $"; host[{slot}]=destroyed/{owner._registeredListeners.Count}"
                        );
                        continue;
                    }
                    GameObject host = owner.gameObject;
                    state.Append(
                        $"; host[{slot}]={(host.activeSelf ? 1 : 0)}/{(host.activeInHierarchy ? 1 : 0)}/{(owner.isActiveAndEnabled ? 1 : 0)}/{owner._registeredListeners.Count}"
                    );
                    state.Append(
                        $"; receiveWhileDisabled[{slot}]={owner.emitMessagesWhenDisabled}"
                    );
                }
                return state.ToString();
            }

            protected override void DisposeAdapterState()
            {
                foreach (MessagingComponent owner in _owners.Components)
                {
                    if (owner != null)
                    {
                        UnityEngine.Object.DestroyImmediate(owner.gameObject);
                    }
                }
            }

            protected override void DisposeToken(int slot)
            {
                if (_owners == null)
                {
                    base.DisposeToken(slot);
                    return;
                }
                try
                {
                    MessagingComponent owner = _owners.Components[slot];
                    if (owner != null)
                    {
                        owner.Release(owner);
                    }
                    // For a destroyed host, observe OnDestroy cleanup without repairing it.
                    Assert.That(
                        owner._registeredListeners,
                        Is.Empty,
                        $"Native owner {slot} must release its token."
                    );
                    Assert.That(
                        Token(slot).Enabled,
                        Is.False,
                        $"Native owner {slot} must disable its released token."
                    );
                    /*
                        The base adapter checks metadata and bus leaks after this returns.
                        Do not repair a failed owner release before those observations.
                    */
                }
                catch
                {
                    base.DisposeToken(slot);
                    throw;
                }
            }
        }
    }
}
#endif
