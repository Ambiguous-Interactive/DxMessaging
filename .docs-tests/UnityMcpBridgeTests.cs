using System.Text.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine.SceneManagement;

namespace WallstopStudios.DxMessaging.Docs.Tests;

[TestFixture]
[NonParallelizable]
internal sealed class UnityMcpBridgeTests
{
    private string _directory = "";
    private string _path = "";
    private string _previousDirectory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dxm-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "result.json");
        _previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _directory;
        SessionState.Values.Clear();
        TestRunnerApi.Active = false;
        TestRunnerApi.QueryThrows = false;
        TestRunnerApi.ExecuteThrows = false;
        TestRunnerApi.Executions = 0;
        TestRunnerApi.DuringExecute = null;
        TestRunnerApi.RunGuid = "owned-guid";
        EditorApplication.isPlayingOrWillChangePlaymode = false;
        EditorApplication.isCompiling = false;
        EditorApplication.isUpdating = false;
        EditorApplication.timeSinceStartup = -1;
        StageUtility.CurrentStage = 0;
        SceneManager.Scenes = [new Scene { path = "Assets/Original.unity", isLoaded = true }];
        SceneManager.ActiveIndex = 0;
    }

    [TearDown]
    public void TearDown()
    {
        SessionState.Values.Clear();
        Environment.CurrentDirectory = _previousDirectory;
        Directory.Delete(_directory, true);
    }

    private string Run() =>
        DxMcpTestRunner.Run("EditMode", "Assembly", "Fixture;Method", "!Perf", _path);

    [Test]
    public void RetainsIdentityAndOwnershipUntilPassiveCleanup()
    {
        Assert.That(Run(), Is.EqualTo("owned-guid"));
        Assert.That(File.ReadAllText(_path + ".run.json"), Does.Contain("owned-guid"));
        Assert.That(
            TestRunnerApi.LastSettings!.Filter.testNames,
            Is.EqualTo("Fixture;Method".Split(';'))
        );
        TestRunnerApi.Callbacks.RunFinished(new Result());
        Assert.That(File.ReadAllText(_path + ".status"), Is.EqualTo("done"));
        EditorApplication.Tick();
        Assert.That(File.ReadAllText(_path + ".cleanup.status"), Is.EqualTo("running"));
        Assert.Throws<InvalidOperationException>(() => Run());
        Assert.That(
            SessionState.GetString("DxMcpTestRunner.RunGuid", ""),
            Is.EqualTo("owned-guid")
        );
        TestRunnerApi.Active = false;
        EditorApplication.Tick();
        Assert.That(File.ReadAllText(_path + ".cleanup.status"), Is.EqualTo("done"));
        Assert.That(File.ReadAllText(_path + ".cleanup.json"), Does.Contain("owned-guid"));
        Assert.That(SessionState.Values, Is.Empty);
        Assert.That(TestRunnerApi.Executions, Is.EqualTo(1));
    }

    [TestCase(TestStatus.Passed)]
    [TestCase(TestStatus.Skipped)]
    public void MixedPassingAndSkippedResultsCompleteCleanup(TestStatus rootStatus)
    {
        Run();
        // Unity propagates ignored children to a skipped suite with positive pass counts.
        Result result = new()
        {
            IsSuite = true,
            TestStatus = rootStatus,
            PassCount = 1,
            SkipCount = 1,
            Children =
            [
                new Result(),
                new Result
                {
                    TestStatus = TestStatus.Skipped,
                    PassCount = 0,
                    SkipCount = 1,
                },
            ],
        };
        TestRunnerApi.Callbacks.RunFinished(result);
        TestRunnerApi.Active = false;
        EditorApplication.Tick();
        Assert.That(
            File.ReadAllText(_path + ".cleanup.status"),
            Is.EqualTo("done"),
            rootStatus.ToString()
        );
        Assert.That(SessionState.Values, Is.Empty);
    }

    [TestCase("observe")]
    [TestCase("error")]
    [TestCase("result")]
    public void UnmarkedLegacyOwnershipPreservesEvidence(string callback)
    {
        SessionState.SetString("DxMcpTestRunner.ResultPath", _path);
        Dictionary<string, string> evidence = new()
        {
            [_path] = "legacy result",
            [_path + ".status"] = "running",
            [_path + ".errors.log"] = "legacy error",
        };
        foreach ((string path, string content) in evidence)
        {
            File.WriteAllText(path, content);
        }
        // Initialize callbacks while proving the unresolved legacy owner blocks Run.
        Assert.Throws<InvalidOperationException>(() => Run());
        switch (callback)
        {
            case "observe":
                EditorApplication.Tick();
                break;
            case "error":
                TestRunnerApi.Callbacks.OnError("unrelated framework error");
                break;
            case "result":
                TestRunnerApi.Callbacks.RunFinished(new Result());
                break;
        }
        foreach ((string path, string content) in evidence)
        {
            Assert.That(File.ReadAllText(path), Is.EqualTo(content), callback + ": " + path);
        }
        Assert.That(Directory.GetFiles(_directory), Is.EquivalentTo(evidence.Keys), callback);
        Assert.That(
            SessionState.GetString("DxMcpTestRunner.ResultPath", ""),
            Is.EqualTo(_path),
            callback
        );
        Assert.That(TestRunnerApi.Executions, Is.Zero);
    }

    [TestCase("ownedResultPath", false)]
    [TestCase("ownedResultPath", true)]
    [TestCase("legacyObserverResultPath", false)]
    public void IncompleteOwnershipRemainsVisibleAndBlocksNewRuns(string field, bool hasRawPath)
    {
        string key =
            field == "ownedResultPath"
                ? "DxMcpTestRunner.OwnedResultPath"
                : "DxMcpObservedTestRunner.ResultPath";
        SessionState.SetString(key, _path);
        if (hasRawPath)
        {
            SessionState.SetString("DxMcpTestRunner.ResultPath", _path + ".different");
        }
        Assert.Throws<InvalidOperationException>(() => Run());
        EditorApplication.timeSinceStartup = double.MaxValue;
        EditorApplication.Tick();
        string snapshot = File.ReadAllText(
            "Packages/com.wallstop-studios.dxmessaging/.artifacts/unity-mcp/editor-state.json"
        );
        Assert.That(snapshot, Does.Contain(field));
        using JsonDocument state = JsonDocument.Parse(snapshot);
        Assert.That(state.RootElement.GetProperty(field).GetString(), Is.EqualTo(_path));
        Assert.That(SessionState.GetString(key, ""), Is.EqualTo(_path));
        Assert.That(TestRunnerApi.Executions, Is.Zero);
    }

    [TestCase("active")]
    [TestCase("compiling")]
    [TestCase("updating")]
    [TestCase("playing")]
    [TestCase("stage")]
    [TestCase("dirty-secondary-scene")]
    [TestCase("unsaved")]
    [TestCase("query-error")]
    [TestCase("legacy-owner")]
    [TestCase("zero-scenes")]
    public void RejectsUnsafeStartWithoutChangingEvidence(string condition)
    {
        switch (condition)
        {
            case "zero-scenes":
                SceneManager.Scenes = [];
                break;
            case "active":
                TestRunnerApi.Active = true;
                break;
            case "compiling":
                EditorApplication.isCompiling = true;
                break;
            case "updating":
                EditorApplication.isUpdating = true;
                break;
            case "playing":
                EditorApplication.isPlayingOrWillChangePlaymode = true;
                break;
            case "stage":
                StageUtility.CurrentStage = 1;
                break;
            case "dirty-secondary-scene":
                SceneManager.Scenes =
                [
                    SceneManager.Scenes[0],
                    new Scene { path = "Assets/Dirty.unity", isDirty = true },
                ];
                break;
            case "unsaved":
                SceneManager.Scenes[0].path = "";
                break;
            case "query-error":
                TestRunnerApi.QueryThrows = true;
                break;
            case "legacy-owner":
                SessionState.SetString("DxMcpObservedTestRunner.ResultPath", "owned");
                break;
        }
        Assert.That(() => Run(), Throws.Exception, condition);
        Assert.That(Directory.GetFiles(_directory), Is.Empty, condition);
        Assert.That(TestRunnerApi.Executions, Is.Zero, condition);
    }

    [TestCase("")]
    [TestCase(".status")]
    [TestCase(".cleanup.status")]
    [TestCase(".run.json")]
    public void RefusesExistingEvidence(string suffix)
    {
        File.WriteAllText(_path + suffix, "preserve");
        Assert.Throws<IOException>(() => Run());
        Assert.That(File.ReadAllText(_path + suffix), Is.EqualTo("preserve"));
        Assert.That(SessionState.Values, Is.Empty);
        Assert.That(TestRunnerApi.Executions, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CapturesFrameworkErrorBeforeOrAfterRawResult(bool resultFirst)
    {
        Run();
        if (resultFirst)
        {
            TestRunnerApi.Callbacks.RunFinished(new Result());
        }
        TestRunnerApi.Callbacks.OnError("cleanup failed");
        EditorApplication.Tick();
        Assert.That(File.ReadAllText(_path + ".cleanup.status"), Is.EqualTo("running"));
        TestRunnerApi.Active = false;
        EditorApplication.Tick();
        Assert.That(File.ReadAllText(_path + ".cleanup.status"), Does.StartWith("error:"));
        Assert.That(File.ReadAllText(_path + ".errors.log"), Does.Contain("cleanup failed"));
        Assert.That(SessionState.Values, Is.Empty);
    }

    [Test]
    public void MissingResultIsTerminalOnlyAfterFrameworkBecomesInactive()
    {
        Run();
        EditorApplication.Tick();
        Assert.That(File.ReadAllText(_path + ".status"), Is.EqualTo("running"));
        TestRunnerApi.Active = false;
        EditorApplication.Tick();
        Assert.That(
            File.ReadAllText(_path + ".cleanup.status"),
            Does.Contain("without a completed result")
        );
        Assert.That(File.ReadAllText(_path + ".status"), Does.StartWith("error:"));
    }

    [Test]
    public void QueryFailureRetainsOwnershipAndRecoversOnNextPassiveObservation()
    {
        Run();
        TestRunnerApi.Callbacks.RunFinished(new Result());
        TestRunnerApi.Active = false;
        TestRunnerApi.QueryThrows = true;
        EditorApplication.Tick();
        Assert.That(
            File.ReadAllText(_path + ".cleanup.status"),
            Does.StartWith("observation-error:")
        );
        Assert.Throws<InvalidOperationException>(() => Run());
        TestRunnerApi.QueryThrows = false;
        EditorApplication.Tick();
        Assert.That(File.ReadAllText(_path + ".cleanup.status"), Is.EqualTo("done"));
    }

    [Test]
    public void ExecuteFailureWithLiveJobRetainsOwnershipUntilCleanup()
    {
        TestRunnerApi.ExecuteThrows = true;
        Assert.Throws<InvalidOperationException>(() => Run());
        EditorApplication.Tick();
        Assert.That(File.ReadAllText(_path + ".cleanup.status"), Is.EqualTo("running"));
        Assert.Throws<InvalidOperationException>(() => Run());
        TestRunnerApi.Active = false;
        EditorApplication.Tick();
        Assert.That(File.ReadAllText(_path + ".cleanup.status"), Does.StartWith("error:"));
        Assert.That(TestRunnerApi.Executions, Is.EqualTo(1));
    }

    [TestCase("dirty")]
    [TestCase("scene-replaced")]
    [TestCase("zero-passes")]
    [TestCase("all-skipped")]
    [TestCase("failed")]
    [TestCase("inconclusive")]
    [TestCase("suite-teardown-failed")]
    [TestCase("suite-inconclusive")]
    [TestCase("child-failed")]
    public void PassingRawCallbackDoesNotHideCleanupOrResultFailure(string condition)
    {
        Run();
        Result result = new();
        switch (condition)
        {
            case "dirty":
                SceneManager.Scenes[0].isDirty = true;
                break;
            case "scene-replaced":
                SceneManager.Scenes[0].path = "Assets/Temporary.unity";
                break;
            case "zero-passes":
                result.PassCount = 0;
                break;
            case "all-skipped":
                result.TestStatus = TestStatus.Skipped;
                result.PassCount = 0;
                result.SkipCount = 1;
                break;
            case "failed":
                result.FailCount = 1;
                break;
            case "inconclusive":
                result.InconclusiveCount = 1;
                break;
            case "suite-teardown-failed":
            case "suite-inconclusive":
                // NUnit suite teardown changes status without incrementing failed-child counts.
                result.IsSuite = true;
                result.TestStatus =
                    condition == "suite-teardown-failed"
                        ? TestStatus.Failed
                        : TestStatus.Inconclusive;
                result.Children = [new Result()];
                break;
            case "child-failed":
                result.IsSuite = true;
                result.Children = [new Result { TestStatus = TestStatus.Failed }];
                break;
        }
        TestRunnerApi.Callbacks.RunFinished(result);
        TestRunnerApi.Active = false;
        EditorApplication.Tick();
        Assert.That(
            File.ReadAllText(_path + ".cleanup.status"),
            Does.StartWith("error:"),
            condition
        );
    }

    [Test]
    public void RetainsTreeOutputAndEscapedFailures()
    {
        Run();
        Result leaf = new()
        {
            FullName = "child",
            TestStatus = TestStatus.Failed,
            Message = "quoted \"value\"\nline",
            Output = "output",
        };
        TestRunnerApi.Callbacks.RunFinished(new Result { IsSuite = true, Children = [leaf] });
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
        JsonElement root = document.RootElement;
        Assert.That(root.GetProperty("nodes").GetArrayLength(), Is.EqualTo(2));
        Assert.That(root.GetProperty("nodes")[1].GetProperty("parentIndex").GetInt32(), Is.Zero);
        Assert.That(
            root.GetProperty("failures")[0].GetProperty("message").GetString(),
            Is.EqualTo(leaf.Message)
        );
        Assert.That(
            root.GetProperty("nodes")[1].GetProperty("output").GetString(),
            Is.EqualTo("output")
        );
    }

    [Test]
    public void SynchronousResultKeepsExecuteIdentityInCompanions()
    {
        TestRunnerApi.DuringExecute = () => TestRunnerApi.Callbacks.RunFinished(new Result());
        Run();
        Assert.That(File.ReadAllText(_path + ".status"), Is.EqualTo("done"));
        Assert.That(File.ReadAllText(_path + ".run.json"), Does.Contain("owned-guid"));
        TestRunnerApi.Active = false;
        EditorApplication.Tick();
        Assert.That(File.ReadAllText(_path + ".cleanup.json"), Does.Contain("owned-guid"));
        Assert.That(File.ReadAllText(_path + ".cleanup.status"), Is.EqualTo("done"));
    }

    [Test]
    public void CallbackStorageFailureStaysContainedAndPreservesOwnership()
    {
        Run();
        Directory.CreateDirectory(_path);
        Directory.CreateDirectory(_path + ".errors.log");
        File.Delete(_path + ".status");
        Directory.CreateDirectory(_path + ".status");
        Assert.DoesNotThrow(() => TestRunnerApi.Callbacks.RunFinished(new Result()));
        Assert.DoesNotThrow(() => TestRunnerApi.Callbacks.OnError("framework failed"));
        File.Delete(_path + ".cleanup.status");
        Directory.CreateDirectory(_path + ".cleanup.status");
        TestRunnerApi.Active = false;
        Assert.DoesNotThrow(() => EditorApplication.Tick());
        Assert.That(SessionState.GetString("DxMcpTestRunner.ResultPath", ""), Is.EqualTo(_path));
        Assert.That(
            SessionState.GetString("DxMcpTestRunner.Errors", ""),
            Does.Contain("framework failed")
        );
        string retainedErrors = SessionState.GetString("DxMcpTestRunner.Errors", "");
        EditorApplication.Tick();
        Assert.That(
            SessionState.GetString("DxMcpTestRunner.Errors", ""),
            Is.EqualTo(retainedErrors)
        );
        Directory.Delete(_path + ".cleanup.status");
        Directory.Delete(_path + ".status");
        EditorApplication.Tick();
        using JsonDocument cleanup = JsonDocument.Parse(File.ReadAllText(_path + ".cleanup.json"));
        Assert.That(
            cleanup.RootElement.GetProperty("frameworkErrors").GetString(),
            Does.Contain("framework failed")
        );
        Assert.That(File.ReadAllText(_path + ".cleanup.status"), Does.StartWith("error:"));
        Assert.That(SessionState.Values, Is.Empty);
    }

    [Test]
    public void PassiveIdleSnapshotCoversEverySceneAndUnknownFrameworkState()
    {
        // Initialize the maintained callback without starting a run.
        Assert.Throws<ArgumentException>(() =>
            DxMcpTestRunner.Run("invalid", null!, null!, null!, _path)
        );
        SceneManager.Scenes =
        [
            SceneManager.Scenes[0],
            new Scene { path = "Assets/Second.unity", isLoaded = true },
        ];
        EditorApplication.timeSinceStartup = double.MaxValue;
        EditorApplication.Tick();
        string snapshot = Path.Combine(
            _directory,
            "Packages/com.wallstop-studios.dxmessaging/.artifacts/unity-mcp/editor-state.json"
        );
        using JsonDocument state = JsonDocument.Parse(File.ReadAllText(snapshot));
        Assert.That(state.RootElement.GetProperty("scenes").GetArrayLength(), Is.EqualTo(2));
        Assert.That(state.RootElement.GetProperty("frameworkActive").GetBoolean(), Is.False);
        Assert.That(state.RootElement.GetProperty("mainStage").GetBoolean(), Is.True);
        TestRunnerApi.QueryThrows = true;
        EditorApplication.Tick();
        using JsonDocument failed = JsonDocument.Parse(File.ReadAllText(snapshot));
        Assert.That(
            failed.RootElement.GetProperty("observationError").GetString(),
            Does.Contain("query failed")
        );
        Assert.That(failed.RootElement.GetProperty("mainStage").GetBoolean(), Is.False);
    }

    private sealed class Result : ITestResultAdaptor, ITestAdaptor
    {
        public ITestAdaptor Test => this;
        public bool IsSuite { get; set; }
        public int PassCount { get; set; } = 1;
        public int FailCount { get; set; }
        public int SkipCount { get; set; }
        public int InconclusiveCount { get; set; }
        public double Duration => 0.125;
        public string FullName { get; set; } = "test";
        public TestStatus TestStatus { get; set; }
        public string ResultState => TestStatus.ToString();
        public string Message { get; set; } = "";
        public string StackTrace => "stack";
        public string Output { get; set; } = "";
        public DateTime StartTime => new(2026, 9, 6);
        public DateTime EndTime => StartTime.AddMilliseconds(125);
        public IEnumerable<ITestResultAdaptor>? Children { get; set; }
    }
}
