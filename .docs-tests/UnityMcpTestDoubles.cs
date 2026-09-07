// Match Unity API shapes, including its event and method names.
#pragma warning disable CA1003, CA1024, CA1724, CA1815, CA1822
// Test doubles for the host-only bridge, linked verbatim from scripts/mcp.
// Tests invoke the maintained bridge; these doubles only drive Unity's external boundaries.
using System.Text.Json;

namespace UnityEngine
{
    internal abstract class ScriptableObject
    {
        public static T CreateInstance<T>()
            where T : new() => new T();
    }

    internal static class JsonUtility
    {
        private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };

        public static string ToJson(object value, bool prettyPrint = false) =>
            JsonSerializer.Serialize(value, Options);

        public static T FromJson<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
    }
}

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class InitializeOnLoadAttribute : Attribute { }

    internal static class SessionState
    {
        internal static readonly Dictionary<string, string> Values = new();

        public static string GetString(string key, string fallback) =>
            Values.GetValueOrDefault(key, fallback);

        public static void SetString(string key, string value) => Values[key] = value;

        public static void EraseString(string key) => Values.Remove(key);
    }

    internal static class EditorApplication
    {
        internal static bool isPlayingOrWillChangePlaymode;
        internal static bool isCompiling;
        internal static bool isUpdating;
        internal static double timeSinceStartup = -1;
        public static event Action? update;

        public static void Tick() => update?.Invoke();
    }
}

namespace UnityEditor.SceneManagement
{
    internal static class StageUtility
    {
        internal static int CurrentStage;

        internal static int GetCurrentStageHandle() => CurrentStage;

        internal static int GetMainStageHandle() => 0;
    }
}

namespace UnityEngine.SceneManagement
{
    internal struct Scene
    {
        public string path;
        public bool isDirty;
        public bool isLoaded;
    }

    internal static class SceneManager
    {
        internal static Scene[] Scenes = [];
        internal static int ActiveIndex;
        internal static int sceneCount => Scenes.Length;

        public static Scene GetSceneAt(int index) => Scenes[index];

        public static Scene GetActiveScene() => Scenes.Length == 0 ? default : Scenes[ActiveIndex];
    }
}

namespace UnityEditor.TestTools.TestRunner.Api
{
    internal enum TestMode
    {
        EditMode,
        PlayMode,
    }

    internal enum TestStatus
    {
        Passed,
        Failed,
        Skipped,
        Inconclusive,
    }

    internal sealed class Filter
    {
        public TestMode testMode;
        public string[]? assemblyNames;
        public string[]? testNames;
        public string[]? categoryNames;
    }

    internal sealed class ExecutionSettings(Filter filter)
    {
        public Filter Filter = filter;
    }

    internal interface ITestAdaptor
    {
        bool IsSuite { get; }
    }

    internal interface ITestResultAdaptor
    {
        ITestAdaptor Test { get; }
        int PassCount { get; }
        int FailCount { get; }
        int SkipCount { get; }
        int InconclusiveCount { get; }
        double Duration { get; }
        string FullName { get; }
        TestStatus TestStatus { get; }
        string ResultState { get; }
        string Message { get; }
        string StackTrace { get; }
        string Output { get; }
        DateTime StartTime { get; }
        DateTime EndTime { get; }
        IEnumerable<ITestResultAdaptor>? Children { get; }
    }

    internal interface ICallbacks
    {
        void RunStarted(ITestAdaptor test);
        void RunFinished(ITestResultAdaptor result);
        void TestStarted(ITestAdaptor test);
        void TestFinished(ITestResultAdaptor result);
    }

    internal interface IErrorCallbacks : ICallbacks
    {
        void OnError(string message);
    }

    internal sealed class TestRunnerApi
    {
        internal static IErrorCallbacks Callbacks = null!;
        internal static bool Active;
        internal static bool QueryThrows;
        internal static bool ExecuteThrows;
        internal static int Executions;
        internal static Action? DuringExecute;
        internal static string RunGuid = "owned-guid";
        internal static ExecutionSettings? LastSettings;

        public void RegisterCallbacks(ICallbacks callbacks) =>
            Callbacks = (IErrorCallbacks)callbacks;

        public string Execute(ExecutionSettings settings)
        {
            Executions++;
            LastSettings = settings;
            Active = true;
            if (ExecuteThrows)
            {
                throw new InvalidOperationException("launch failed with live job");
            }
            DuringExecute?.Invoke();
            return RunGuid;
        }

        internal static bool IsRunActive() =>
            QueryThrows ? throw new InvalidOperationException("query failed") : Active;
    }
}
