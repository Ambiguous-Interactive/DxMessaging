#if UNITY_EDITOR && DXM_621_PIPELINE_PRESENT
namespace DxMessaging.Tests.Editor.PipelineTransportResearch
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;
    using Unity.Pipeline;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class PipelineLifecycleTests : UnityFixtureBase
    {
        private readonly List<TransportOwner> _owners = new();
        private static PipelineLifecycleTests _retainedFixture;
        private const string Token = "dxm-621-fixture-token";

        private static Task _phaseControlTask;

        [Serializable]
        private sealed class PhaseControlResult
        {
            public string Outcome;
            public string Failure;
            public List<PhaseControlRow> Rows = new();
        }

        [Serializable]
        private sealed class PhaseControlRow
        {
            public string Kind;
            public int Index;
            public string PeerBefore;
            public string Phase;
            public string OperationFailure;
            public string[] TaskTerminalErrors;
            public int StatusCalls;
            public int CommandMetadataCalls;
            public string Response;
            public string[] Errors;
            public string[] ResponseEvents;
            public int AdmissionEvents;
            public int ReleaseCount;
            public int PendingTasks;
            public bool PortRebound;
        }

        public static string StartOwnedResponsePhaseControls(
            bool expectResponseCandidate,
            bool negativeOnly = false,
            bool closedResponseOnly = false
        )
        {
            if (_phaseControlTask != null && !_phaseControlTask.IsCompleted)
                throw new InvalidOperationException("Owned phase diagnostic is already active.");
            string path = Path.GetFullPath(
                Path.Combine(
                    Application.dataPath,
                    "../Packages/com.wallstop-studios.dxmessaging/.artifacts/perf-lab/621-response-ownership-s397/"
                        + (
                            closedResponseOnly
                                ? (
                                    expectResponseCandidate
                                        ? "closed-response-candidate-polling.json"
                                        : "closed-response-baseline-polling.json"
                                )
                            : negativeOnly
                                ? "candidate-handler-negative-controls-final-polling.json"
                            : expectResponseCandidate ? "candidate-controls-final-polling.json"
                            : "baseline-controls-v2-polling.json"
                        )
                )
            );
            if (File.Exists(path) || File.Exists(path + ".status"))
                throw new InvalidOperationException(
                    "Owned phase evidence must not be overwritten."
                );
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path + ".status", "running");
            _phaseControlTask = RunControls();
            async Task RunControls()
            {
                PhaseControlResult result = new();
                try
                {
                    foreach (
                        string kind in closedResponseOnly ? new[] { "CommandsResponseClosed" }
                        : negativeOnly
                            ? new[]
                            {
                                "CommandsIoFault",
                                "CommandsDisposedFault",
                                "CommandsListenerFault",
                            }
                        : new[]
                        {
                            "HandlerFault",
                            "CommandsMetadataFault",
                            "CommandsSocketDisposed",
                            "CommandsHeadersSent",
                            "LiveCommands",
                        }
                    )
                    {
                        int count = 50;
                        for (int index = 0; index < count; ++index)
                        {
                            TransportOwner owner = new() { OutsideNUnitDiagnostic = true };
                            PhaseControlRow row = new()
                            {
                                Kind = kind,
                                Index = index,
                                Phase = "create",
                            };
                            try
                            {
                                Task<HttpListenerContext> accept = owner.Observe(
                                    owner.Listener.GetContextAsync()
                                );
                                await WithinOperationDeadline(
                                    owner.Observe(
                                        owner.Client.ConnectAsync(IPAddress.Loopback, owner.Port)
                                    )
                                );
                                string endpoint =
                                    kind == "HandlerFault"
                                        ? "/api/status"
                                        : "/api/commands?detail=compact";
                                byte[] request = Encoding.ASCII.GetBytes(
                                    $"GET {endpoint} HTTP/1.1\r\nHost: 127.0.0.1:{owner.Port}\r\nAuthorization: Bearer {Token}\r\nConnection: close\r\n\r\n"
                                );
                                await WithinOperationDeadline(
                                    owner.Observe(
                                        owner
                                            .Client.GetStream()
                                            .WriteAsync(request, 0, request.Length)
                                    )
                                );
                                await WithinOperationDeadline(accept);
                                owner.Context = accept.Result;
                                row.PeerBefore =
                                    owner.Context.Request.RemoteEndPoint.Address.ToString();
                                Assert.That(row.PeerBefore, Is.EqualTo("127.0.0.1"));
                                if (kind == "HandlerFault")
                                    owner.Server.StatusControl = () =>
                                        throw new InvalidOperationException(
                                            "controlled-handler-fault-621"
                                        );
                                if (kind == "CommandsMetadataFault")
                                    owner.Server.CommandMetadataControl = () =>
                                        throw new InvalidOperationException(
                                            "controlled-metadata-fault-621"
                                        );
                                if (kind == "CommandsIoFault")
                                    owner.Server.CommandMetadataControl = () =>
                                        throw new IOException("controlled-metadata-fault-621");
                                if (kind == "CommandsDisposedFault")
                                    owner.Server.CommandMetadataControl = () =>
                                        throw new ObjectDisposedException(
                                            "controlled-metadata-fault-621"
                                        );
                                if (kind == "CommandsListenerFault")
                                    owner.Server.CommandMetadataControl = () =>
                                        throw new HttpListenerException(
                                            5,
                                            "controlled-metadata-fault-621"
                                        );
                                if (kind == "CommandsResponseClosed")
                                    owner.Server.CommandMetadataControl = () =>
                                        owner.Context.Response.Abort();
                                if (kind == "CommandsSocketDisposed")
                                    owner.Server.CommandMetadataControl = () =>
                                    {
                                        object connection = typeof(HttpListenerContext)
                                            .GetProperty(
                                                "Connection",
                                                BindingFlags.Instance | BindingFlags.NonPublic
                                            )
                                            .GetValue(owner.Context);
                                        Socket socket = (Socket)
                                            connection
                                                .GetType()
                                                .GetField(
                                                    "sock",
                                                    BindingFlags.Instance | BindingFlags.NonPublic
                                                )
                                                .GetValue(connection);
                                        Assert.That(
                                            ((IPEndPoint)socket.LocalEndPoint).Port,
                                            Is.EqualTo(owner.Port)
                                        );
                                        Assert.That(
                                            ((IPEndPoint)socket.RemoteEndPoint).Address,
                                            Is.EqualTo(IPAddress.Loopback)
                                        );
                                        socket.Dispose();
                                    };
                                if (kind == "CommandsHeadersSent")
                                    owner.Server.CommandMetadataControl = () =>
                                    {
                                        owner.Context.Response.SendChunked = true;
                                        owner.Context.Response.OutputStream.Write(
                                            new byte[] { 120 },
                                            0,
                                            1
                                        );
                                    };
                                Task<string> read = owner.Observe(
                                    new StreamReader(
                                        owner.Client.GetStream(),
                                        Encoding.UTF8
                                    ).ReadToEndAsync()
                                );
                                row.Phase = "dispatch";
                                owner.BeginObservation();
                                MethodInfo actual = typeof(BasePipelineServer).GetMethod(
                                    "ProcessRequestDetached",
                                    BindingFlags.Instance | BindingFlags.NonPublic
                                );
                                await WithinOperationDeadline(
                                    owner.Observe(
                                        (Task)
                                            actual.Invoke(
                                                owner.Server,
                                                new object[] { owner.Context }
                                            )
                                    )
                                );
                                row.Phase = "post-dispatch-abort";
                                owner.Context.Response.Abort();
                                row.Phase = "client-read";
                                await WithinOperationDeadline(read);
                                row.Response = read.Result;
                                owner.EndObservation();
                                row.StatusCalls = owner.Server.StatusCalls;
                                row.CommandMetadataCalls = owner.Server.CommandMetadataCalls;
                                row.Errors = owner.Errors.ToArray();
                                row.ResponseEvents = owner.ResponseEvents.ToArray();
                                row.AdmissionEvents = owner.AdmissionEvents.Count;
                                Assert.That(
                                    row.AdmissionEvents,
                                    Is.Zero,
                                    "These actions occur after actual peer admission."
                                );
                                if (kind == "HandlerFault")
                                {
                                    Assert.That(row.StatusCalls, Is.EqualTo(1));
                                    Assert.That(row.Errors.Length, Is.EqualTo(1));
                                    Assert.That(
                                        row.Errors[0],
                                        Does.Contain("controlled-handler-fault-621")
                                    );
                                    Assert.That(row.Response, Does.StartWith("HTTP/1.1 400"));
                                }
                                else
                                {
                                    Assert.That(row.CommandMetadataCalls, Is.EqualTo(1));
                                    if (kind == "LiveCommands")
                                    {
                                        Assert.That(row.Response, Does.StartWith("HTTP/1.1 200"));
                                        Assert.That(row.Errors, Is.Empty);
                                    }
                                    else if (kind == "CommandsMetadataFault" || negativeOnly)
                                    {
                                        Assert.That(row.Errors.Length, Is.EqualTo(1));
                                        Assert.That(
                                            row.Errors[0],
                                            Does.Contain("controlled-metadata-fault-621")
                                        );
                                        Assert.That(row.Response, Does.StartWith("HTTP/1.1 500"));
                                        Assert.That(row.ResponseEvents, Is.Empty);
                                        if (expectResponseCandidate)
                                        {
                                            string expectedType = kind switch
                                            {
                                                "CommandsIoFault" => "System.IO.IOException",
                                                "CommandsDisposedFault" =>
                                                    "System.ObjectDisposedException",
                                                "CommandsListenerFault" =>
                                                    "System.Net.HttpListenerException",
                                                _ => "System.InvalidOperationException",
                                            };
                                            Assert.That(row.Errors[0], Does.Contain(expectedType));
                                        }
                                    }
                                    else if (closedResponseOnly)
                                    {
                                        Assert.That(
                                            row.Errors.Length,
                                            Is.EqualTo(expectResponseCandidate ? 0 : 1)
                                        );
                                        Assert.That(
                                            row.ResponseEvents.Length,
                                            Is.EqualTo(expectResponseCandidate ? 1 : 0)
                                        );
                                        string actualFailure = expectResponseCandidate
                                            ? row.ResponseEvents[0]
                                            : row.Errors[0];
                                        Assert.That(
                                            actualFailure,
                                            Does.Contain("System.ObjectDisposedException")
                                        );
                                        Assert.That(actualFailure, Does.Contain("SendJsonPayload"));
                                    }
                                    else if (
                                        expectResponseCandidate
                                        && kind == "CommandsSocketDisposed"
                                    )
                                    {
                                        Assert.That(row.Errors, Is.Empty);
                                        Assert.That(row.ResponseEvents.Length, Is.EqualTo(1));
                                        Assert.That(
                                            row.ResponseEvents[0],
                                            Does.Contain("System.IO.IOException")
                                        );
                                        Assert.That(
                                            row.ResponseEvents[0],
                                            Does.Contain("SendJsonPayload")
                                        );
                                    }
                                    else
                                    {
                                        Assert.That(
                                            row.Errors.Length,
                                            Is.EqualTo(expectResponseCandidate ? 1 : 2)
                                        );
                                        if (expectResponseCandidate)
                                            Assert.That(
                                                row.Errors[0],
                                                Does.Contain("System.InvalidOperationException")
                                            );
                                        Assert.That(row.ResponseEvents, Is.Empty);
                                    }
                                }
                            }
                            catch (Exception operationError)
                            {
                                row.OperationFailure = operationError.ToString();
                                throw;
                            }
                            finally
                            {
                                owner.Client.Close();
                                owner.Server.Stop();
                                owner.Listener.Close();
                                List<string> taskErrors = new();
                                foreach (Task task in owner._tasks)
                                {
                                    await WithinOperationDeadline(
                                        task,
                                        propagateTaskFailure: false
                                    );
                                    if (task.IsFaulted)
                                        taskErrors.Add(task.Exception.ToString());
                                }
                                row.TaskTerminalErrors = taskErrors.ToArray();
                                owner.EndObservation();
                                row.StatusCalls = owner.Server.StatusCalls;
                                row.CommandMetadataCalls = owner.Server.CommandMetadataCalls;
                                row.Errors = owner.Errors.ToArray();
                                row.ResponseEvents = owner.ResponseEvents.ToArray();
                                row.AdmissionEvents = owner.AdmissionEvents.Count;
                                result.Rows.Add(row);
                                File.WriteAllText(path, JsonUtility.ToJson(result, true));
                                owner.Dispose();
                                owner.Dispose();
                                row.ReleaseCount = owner.ReleaseCount;
                                row.PendingTasks = owner.PendingCount;
                                Assert.That(row.ReleaseCount, Is.EqualTo(1));
                                Assert.That(row.PendingTasks, Is.Zero);
                                Assert.That(owner.Server.IsRunning, Is.False);
                                Assert.That(owner.ObservationAttached, Is.False);
                                TcpListener proof = new(IPAddress.Loopback, owner.Port);
                                try
                                {
                                    proof.Start();
                                    row.PortRebound = true;
                                }
                                finally
                                {
                                    proof.Stop();
                                }
                                File.WriteAllText(path, JsonUtility.ToJson(result, true));
                            }
                        }
                    }
                    result.Outcome = "observed";
                }
                catch (Exception error)
                {
                    result.Outcome = "unexpected-failure";
                    result.Failure = error.ToString();
                }
                finally
                {
                    File.WriteAllText(path, JsonUtility.ToJson(result, true));
                    File.WriteAllText(
                        path + ".status",
                        result.Outcome == "observed" ? "done" : "error"
                    );
                }
            }
            return path;
        }

        private static async Task WithinOperationDeadline(
            Task task,
            bool propagateTaskFailure = true
        )
        {
            Stopwatch deadline = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (5000 <= deadline.ElapsedMilliseconds)
                    throw new TimeoutException(
                        "Owned diagnostic operation exceeded the existing five-second deadline."
                    );
                await Task.Yield();
            }
            if (propagateTaskFailure)
                task.GetAwaiter().GetResult();
        }

        public sealed class Case
        {
            internal readonly string Kind;
            internal readonly int Index;
            internal readonly bool ProbeCleanup;

            internal Case(string kind, int index = 0, bool probeCleanup = false)
            {
                Kind = kind;
                Index = index;
                ProbeCleanup = probeCleanup;
            }

            public override string ToString() => $"{Kind}-{Index}";
        }

        public static IEnumerable<Case> Cases
        {
            get
            {
                for (int i = 0; i < 50; ++i)
                    yield return new Case("ClosedResponse", i);
                for (int i = 0; i < 50; ++i)
                    yield return new Case("SocketDisposed", i);
                foreach (
                    string kind in new[]
                    {
                        "Authorized",
                        "MissingToken",
                        "WrongToken",
                        "UntrustedOrigin",
                        "Shutdown",
                    }
                )
                    yield return new Case(kind);
            }
        }

        [OneTimeSetUp]
        public void RetainFixtureUntilResourcesRelease()
        {
            Assert.That(_retainedFixture, Is.Null);
            _retainedFixture = this;
            Assert.That(
                typeof(BasePipelineServer).Assembly.GetName().Name,
                Is.EqualTo("Unity.Pipeline")
            );
        }

        [OneTimeTearDown]
        public void RetireFixtureAfterResourcesRelease()
        {
            Assert.That(_owners, Is.Empty);
            _retainedFixture = null;
        }

        public override void TearDownManagedResources()
        {
            List<Exception> failures = new();
            foreach (TransportOwner owner in _owners)
            {
                try
                {
                    owner.ReportTerminalState();
                    owner.Dispose();
                    owner.Dispose();
                    Assert.That(owner.ReleaseCount, Is.EqualTo(1));
                    Assert.That(owner.Server.IsRunning, Is.False);
                    Assert.That(owner.ObservationAttached, Is.False);
                    Assert.That(owner.PendingCount, Is.Zero);
                    TcpListener proof = new(IPAddress.Loopback, owner.Port);
                    try
                    {
                        proof.Start();
                    }
                    finally
                    {
                        proof.Stop();
                    }
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
            TestContext.WriteLine(
                $"Pipeline fixture cleanup: owners={_owners.Count},allKnownTasksObserved=true; ports rebound; each owner released once."
            );
            if (failures.Count == 0)
                _owners.Clear();
            base.TearDownManagedResources();
            if (failures.Count != 0)
                throw new AggregateException(failures);
        }

        [UnityTest]
        public IEnumerator ActualDetachedRequestRespectsContextLifetimeAndAuthorization(
            [ValueSource(nameof(Cases))] Case scenario
        )
        {
            TransportOwner owner = TrackDisposable(new TransportOwner());
            _owners.Add(owner);
            owner.Scenario = scenario;
            Task<HttpListenerContext> accept = owner.Observe(owner.Listener.GetContextAsync());
            Task connect = owner.Observe(owner.Client.ConnectAsync(IPAddress.Loopback, owner.Port));
            yield return Await(connect);
            string authorization =
                scenario.Kind == "MissingToken"
                    ? ""
                    : $"Authorization: Bearer {(scenario.Kind == "WrongToken" ? "wrong-fixture-token" : Token)}\r\n";
            string origin =
                scenario.Kind == "UntrustedOrigin" ? "Origin: https://untrusted.invalid\r\n" : "";
            byte[] request = Encoding.ASCII.GetBytes(
                $"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:{owner.Port}\r\n{authorization}{origin}Connection: close\r\n\r\n"
            );
            yield return Await(
                owner.Observe(owner.Client.GetStream().WriteAsync(request, 0, request.Length))
            );
            yield return Await(accept);
            owner.Context = accept.Result;
            owner.ResponseBefore = ObserveResponse(owner.Context);
            string peerBefore = owner.Context.Request.RemoteEndPoint?.Address.ToString();
            owner.PeerBefore = peerBefore;
            Assert.That(peerBefore, Is.EqualTo("127.0.0.1"));
            if (scenario.Kind == "ClosedResponse")
            {
                owner.Context.Response.KeepAlive = false;
                owner.Context.Response.ContentLength64 = 0;
                owner.Context.Response.Close();
                Assert.That(
                    owner.Server.IsRunning,
                    Is.True,
                    "The listener remains active while this context terminates."
                );
            }
            else if (scenario.Kind == "SocketDisposed" || scenario.Kind == "SocketShutdown")
            {
                object connection = typeof(HttpListenerContext)
                    .GetProperty("Connection", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(owner.Context);
                Socket socket = (Socket)
                    connection
                        .GetType()
                        .GetField("sock", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(connection);
                Assert.That(((IPEndPoint)socket.LocalEndPoint).Port, Is.EqualTo(owner.Port));
                Assert.That(
                    ((IPEndPoint)socket.RemoteEndPoint).Address,
                    Is.EqualTo(IPAddress.Loopback)
                );
                if (scenario.Kind == "SocketDisposed")
                    socket.Dispose();
                else
                    socket.Shutdown(SocketShutdown.Both);
                Assert.That(owner.Server.IsRunning, Is.True);
            }
            else if (scenario.Kind == "Shutdown")
                owner.Server.Stop();
            string peerAfter = null;
            string endpointError = null;
            try
            {
                peerAfter = owner.Context.Request.RemoteEndPoint?.Address.ToString();
            }
            catch (Exception error)
            {
                endpointError = error.ToString();
                if (error is SocketException socketError)
                    endpointError +=
                        $"\nSocketErrorCode={socketError.SocketErrorCode},NativeErrorCode={socketError.NativeErrorCode},ErrorCode={socketError.ErrorCode}";
            }
            owner.PeerAfter = peerAfter;
            owner.EndpointError = endpointError;
            owner.ResponseAfter = ObserveResponse(owner.Context);
            if (scenario.ProbeCleanup)
            {
                TestContext.WriteLine("Pipeline abort endpoint: " + (endpointError ?? "none"));
                owner.Context.Response.Abort();
                TestContext.WriteLine(
                    $"Pipeline abort completed: kind={scenario.Kind},active={owner.Server.IsRunning},handlerCalls={owner.Server.StatusCalls}"
                );
                Assert.That(owner.Server.IsRunning, Is.True);
                Assert.That(owner.Server.StatusCalls, Is.Zero);
                yield break;
            }
            owner.BeginObservation();
            try
            {
                MethodInfo actual = typeof(BasePipelineServer).GetMethod(
                    "ProcessRequestDetached",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
                Assert.That(actual, Is.Not.Null);
                Task handling = owner.Observe(
                    (Task)actual.Invoke(owner.Server, new object[] { owner.Context })
                );
                yield return Await(handling);
            }
            finally
            {
                owner.EndObservation();
            }
            string response = "terminal context";
            if (scenario.Kind != "Shutdown")
            {
                Task<string> read = owner.Observe(
                    new StreamReader(owner.Client.GetStream(), Encoding.UTF8).ReadToEndAsync()
                );
                yield return Await(read);
                response = read.Result;
            }
            int expectedCalls = scenario.Kind == "Authorized" ? 1 : 0;
            TestContext.WriteLine(
                $"Pipeline lifecycle row: kind={scenario.Kind},index={scenario.Index},active={owner.Server.IsRunning},peerBefore={peerBefore},peerAfter={peerAfter ?? "null"},statusCalls={owner.Server.StatusCalls},errors={owner.Errors.Count}."
            );
            TestContext.WriteLine("Pipeline endpoint observation: " + (endpointError ?? "none"));
            foreach (string error in owner.Errors)
                TestContext.WriteLine("Pipeline actual error: " + error);
            Assert.That(
                owner.Server.StatusCalls,
                Is.EqualTo(expectedCalls),
                "Only a live authorized request reaches the real status handler."
            );
            if (scenario.Kind == "Authorized")
                Assert.That(response, Does.StartWith("HTTP/1.1 200"));
            if (scenario.Kind == "MissingToken" || scenario.Kind == "WrongToken")
                Assert.That(response, Does.StartWith("HTTP/1.1 401"));
            if (scenario.Kind == "UntrustedOrigin")
                Assert.That(response, Does.StartWith("HTTP/1.1 403"));
            Assert.That(
                owner.Errors,
                Is.Empty,
                "Terminal contexts must not cause an unhandled error; all logs remain observable."
            );
            int expectedRejections =
                scenario.Kind == "ClosedResponse"
                || scenario.Kind == "SocketDisposed"
                || scenario.Kind == "SocketShutdown"
                || scenario.Kind == "Shutdown"
                    ? 1
                    : 0;
            Assert.That(
                owner.AdmissionEvents.Count,
                Is.EqualTo(expectedRejections),
                "Terminated contexts must retain one explicit pre-admission rejection event."
            );
        }

        public static IEnumerable<Case> AbortCases
        {
            get
            {
                foreach (
                    string kind in new[] { "ClosedResponse", "SocketDisposed", "SocketShutdown" }
                )
                    yield return new Case(kind, probeCleanup: true);
            }
        }

        [UnityTest]
        public IEnumerator ActualAbortReleasesTerminatedOwnedContext(
            [ValueSource(nameof(AbortCases))] Case scenario
        )
        {
            yield return ActualDetachedRequestRespectsContextLifetimeAndAuthorization(scenario);
        }

        public static IEnumerable<Case> ShutdownCases
        {
            get
            {
                for (int index = 0; index < 50; ++index)
                    yield return new Case("SocketShutdown", index);
            }
        }

        /// <remarks>
        /// October 8, 2026: socket shutdown and disposal are separate lifecycle phases.
        /// Keep the actual endpoint, dispatch and response failures attributable by phase.
        /// </remarks>
        [UnityTest]
        public IEnumerator ActualDetachedRequestRejectsShutdownSocket(
            [ValueSource(nameof(ShutdownCases))] Case scenario
        )
        {
            yield return ActualDetachedRequestRespectsContextLifetimeAndAuthorization(scenario);
        }

        public static IEnumerable<Case> AcceptCases
        {
            get
            {
                foreach (string kind in new[] { "CapturedAccept", "ThreadPoolAccept" })
                    for (int index = 0; index < 50; ++index)
                        yield return new Case(kind, index);
            }
        }

        /// <remarks>
        /// October 8, 2026: actual Pipeline admission awaits GetContextAsync.
        /// This assay distinguishes caller-context capture from thread-pool admission;
        /// it does not establish the cause or frequency of naturally expired sockets.
        /// </remarks>
        [UnityTest]
        public IEnumerator ActualAcceptLoopUsesExpectedSynchronizationContext(
            [ValueSource(nameof(AcceptCases))] Case scenario
        )
        {
            SynchronizationContext original = SynchronizationContext.Current;
            Assert.That(original, Is.Not.Null, "A live Unity caller context is required.");
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            TransportOwner owner = TrackDisposable(new TransportOwner());
            _owners.Add(owner);
            owner.Scenario = scenario;
            RecordingContext recording = new(original);
            MethodInfo actual = typeof(BasePipelineServer).GetMethod(
                "HandleRequests",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(actual, Is.Not.Null);
            Task loop;
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    scenario.Kind == "CapturedAccept" ? recording : null
                );
                loop = owner.Observe((Task)actual.Invoke(owner.Server, null));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(original);
            }
            Assert.That(SynchronizationContext.Current, Is.SameAs(original));
            Assert.That(loop.IsCompleted, Is.False, "Accept must begin before a request exists.");
            Task<string> read = null;
            owner.BeginObservation();
            try
            {
                yield return Await(
                    owner.Observe(owner.Client.ConnectAsync(IPAddress.Loopback, owner.Port))
                );
                byte[] request = Encoding.ASCII.GetBytes(
                    $"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:{owner.Port}\r\nAuthorization: Bearer {Token}\r\nConnection: close\r\n\r\n"
                );
                yield return Await(
                    owner.Observe(owner.Client.GetStream().WriteAsync(request, 0, request.Length))
                );
                read = owner.Observe(
                    new StreamReader(owner.Client.GetStream(), Encoding.UTF8).ReadToEndAsync()
                );
                yield return Await(read);
                owner.AcceptDiagnostic =
                    $"callerThread={callerThread},handlerThread={owner.Server.StatusThread},callerContext={original.GetType().FullName},handlerContext={owner.Server.StatusContext},postsBeforeStop={recording.PostCount},loopPendingBeforeStop={!loop.IsCompleted}";
            }
            finally
            {
                owner.Server.Stop();
                owner.EndObservation();
            }
            yield return Await(loop);
            TestContext.WriteLine("Pipeline accept row: " + owner.AcceptDiagnostic);
            Assert.That(read.Result, Does.StartWith("HTTP/1.1 200"));
            Assert.That(owner.Server.StatusCalls, Is.EqualTo(1));
            Assert.That(owner.Errors, Is.Empty);
            Assert.That(owner.Server.IsRunning, Is.False);
            Assert.That(owner.PendingCount, Is.Zero);
            if (scenario.Kind == "CapturedAccept")
            {
                Assert.That(1, Is.LessThanOrEqualTo(recording.PostCount));
                Assert.That(owner.Server.StatusThread, Is.EqualTo(callerThread));
            }
            else
            {
                Assert.That(recording.PostCount, Is.Zero);
                Assert.That(owner.Server.StatusThread, Is.Not.EqualTo(callerThread));
                Assert.That(owner.Server.StatusContext, Is.EqualTo("null"));
            }
        }

        private sealed class RecordingContext : SynchronizationContext
        {
            private readonly SynchronizationContext _target;
            private int _posts;
            internal int PostCount => Volatile.Read(ref _posts);

            internal RecordingContext(SynchronizationContext target) => _target = target;

            public override void Post(SendOrPostCallback callback, object state)
            {
                Interlocked.Increment(ref _posts);
                _target.Post(callback, state);
            }
        }

        private static string ObserveResponse(HttpListenerContext context)
        {
            try
            {
                return "CanWrite=" + context.Response.OutputStream.CanWrite;
            }
            catch (Exception error)
            {
                return error.GetType().FullName + ": " + error.Message;
            }
        }

        private static IEnumerator Await(Task task)
        {
            Stopwatch deadline = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                Assert.That(
                    deadline.ElapsedMilliseconds,
                    Is.LessThan(5000),
                    "Owned networking must terminate within the bounded operation deadline."
                );
                yield return null;
            }
            task.GetAwaiter().GetResult();
        }

        private sealed class FixtureServer : BasePipelineServer
        {
            internal int StatusCalls;
            internal int StatusThread;
            internal string StatusContext;
            internal Action StatusControl;
            internal Action CommandMetadataControl;
            internal int CommandMetadataCalls;
            private readonly DateTime _startedAt = DateTime.UtcNow;
            public override DateTime StartedAt
            {
                get
                {
                    Interlocked.Increment(ref CommandMetadataCalls);
                    CommandMetadataControl?.Invoke();
                    return _startedAt;
                }
            }
            protected override bool WritesDescriptor => false;

            protected override void CreateInstanceDescriptor() =>
                throw new InvalidOperationException("Fixture descriptor write forbidden.");

            protected override void DeleteInstanceDescriptor() =>
                throw new InvalidOperationException("Fixture descriptor delete forbidden.");

            protected override void UpdateHeartBeat() { }

            protected override string GetToken() => Token;

            protected override object GetServerStatus()
            {
                Interlocked.Increment(ref StatusCalls);
                StatusControl?.Invoke();
                StatusThread = Thread.CurrentThread.ManagedThreadId;
                StatusContext = SynchronizationContext.Current?.GetType().FullName ?? "null";
                return new { fixture = "owned-loopback" };
            }
        }

        private sealed class TransportOwner : IDisposable
        {
            internal readonly HttpListener Listener = new();
            internal readonly TcpClient Client = new();
            internal readonly FixtureServer Server = new();
            internal readonly int Port;
            internal readonly List<string> Errors = new();
            internal readonly List<string> AdmissionEvents = new();
            internal readonly List<string> ResponseEvents = new();
            internal HttpListenerContext Context;
            internal Case Scenario;
            internal string AcceptDiagnostic;
            internal string PeerBefore,
                PeerAfter,
                EndpointError,
                ResponseBefore,
                ResponseAfter;
            internal readonly List<Task> _tasks = new();
            internal bool OutsideNUnitDiagnostic;
            private readonly object _logGate = new();
            internal int ReleaseCount { get; private set; }
            internal bool ObservationAttached { get; private set; }
            internal int PendingCount
            {
                get
                {
                    int pending = 0;
                    foreach (Task task in _tasks)
                        if (!task.IsCompleted)
                            ++pending;
                    return pending;
                }
            }

            internal TransportOwner()
            {
                TcpListener reservation = new(IPAddress.Loopback, 0);
                try
                {
                    reservation.Start();
                    Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                }
                finally
                {
                    reservation.Stop();
                }
                try
                {
                    Listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                    Listener.Start();
                    Type actual = typeof(BasePipelineServer);
                    actual
                        .GetField("m_HttpListener", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(Server, Listener);
                    actual
                        .GetField("m_Port", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(Server, Port);
                    actual
                        .GetField("m_IsRunning", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(Server, true);
                    Assert.That(Server.IsRunning, Is.True);
                    Assert.That(Server.WatchdogEnabled, Is.False);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal T Observe<T>(T task)
                where T : Task
            {
                _tasks.Add(task);
                return task;
            }

            internal void BeginObservation()
            {
                Application.logMessageReceivedThreaded += Capture;
                ObservationAttached = true;
            }

            internal void EndObservation()
            {
                if (!ObservationAttached)
                    return;
                Application.logMessageReceivedThreaded -= Capture;
                ObservationAttached = false;
            }

            private void Capture(string text, string stack, LogType kind)
            {
                if (
                    kind == LogType.Log
                    && text.StartsWith(
                        "Pipeline: rejected request before peer admission because transport ended:",
                        StringComparison.Ordinal
                    )
                )
                {
                    lock (_logGate)
                        AdmissionEvents.Add(text + "\n" + stack);
                }
                if (
                    kind == LogType.Log
                    && text.StartsWith(
                        "Pipeline: response transport ended during JSON ",
                        StringComparison.Ordinal
                    )
                )
                {
                    lock (_logGate)
                        ResponseEvents.Add(text + "\n" + stack);
                }
                if (kind != LogType.Error && kind != LogType.Exception && kind != LogType.Assert)
                    return;
                lock (_logGate)
                    Errors.Add(kind + ": " + text + "\n" + stack);
            }

            internal void ReportTerminalState()
            {
                EndObservation();
                lock (_logGate)
                {
                    TestContext.WriteLine(
                        $"Pipeline teardown row: kind={Scenario?.Kind},index={Scenario?.Index},active={Server.IsRunning},peerBefore={PeerBefore},peerAfter={PeerAfter ?? "null"},statusCalls={Server.StatusCalls},errors={Errors.Count},responseBefore={ResponseBefore},responseAfter={ResponseAfter}."
                    );
                    TestContext.WriteLine("Pipeline admission events: " + AdmissionEvents.Count);
                    foreach (string admission in AdmissionEvents)
                        TestContext.WriteLine("Pipeline actual admission: " + admission);
                    TestContext.WriteLine(
                        "Pipeline accept teardown: " + (AcceptDiagnostic ?? "none")
                    );
                    TestContext.WriteLine(
                        "Pipeline teardown endpoint: " + (EndpointError ?? "none")
                    );
                    foreach (string error in Errors)
                        TestContext.WriteLine("Pipeline teardown actual error: " + error);
                }
            }

            public void Dispose()
            {
                if (ReleaseCount != 0)
                    return;
                EndObservation();
                Client.Close();
                Server.Stop();
                Listener.Close();
                foreach (Task task in _tasks)
                {
                    if (!task.IsCompleted)
                        task.Wait(5000);
                    Assert.That(
                        task.IsCompleted,
                        Is.True,
                        "Disposal must terminate every known task."
                    );
                    if (task.IsFaulted && !OutsideNUnitDiagnostic)
                        TestContext.WriteLine("Pipeline owned task fault: " + task.Exception);
                }
                ++ReleaseCount;
            }
        }
    }
}
#endif
