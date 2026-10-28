#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using DxMessaging.Editor.Windows;
    using UnityEditor;

    internal static class EditorSourceIndexInspection
    {
        private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type Owner = typeof(DxMessagingEditorSourceLinks);
        private static IDictionary Indexes =>
            (IDictionary)
                Owner.GetField("MessageSourceAssemblyIndexes", StaticMembers).GetValue(null);
        private static MethodInfo DrainMethod =>
            Owner.GetMethod("DrainMessageSourceAssemblyIndexes", StaticMembers);

        internal static int PendingCount =>
            Indexes.Values.Cast<object>().Count(index => BuildTask(index) != null);
        internal static int CompletedCount =>
            Indexes.Values.Cast<object>().Count(index => BuildTask(index)?.IsCompleted == true);

        private static Task BuildTask(object index) =>
            (Task)index.GetType().GetProperty("BuildTask", InstanceMembers).GetValue(index);

        internal static Task BuildTaskFor(string assemblyName) => BuildTask(Indexes[assemblyName]);

        internal static void Drain() => DrainMethod.Invoke(null, null);

        internal static void Complete()
        {
            Task.WaitAll(
                Indexes
                    .Values.Cast<object>()
                    .Select(BuildTask)
                    .Where(task => task != null)
                    .ToArray()
            );
            Drain();
        }

        internal static void Reset()
        {
            Complete();
            EditorApplication.CallbackFunction callback = (EditorApplication.CallbackFunction)
                Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction), DrainMethod);
            EditorApplication.update -= callback;
            Indexes.Clear();
            (
                (IDictionary)
                    Owner.GetField("MessageSourceLocationCache", StaticMembers).GetValue(null)
            ).Clear();
        }

        internal static object ReadFiles(params (string path, string assetPath)[] files)
        {
            Type fileType = Owner.GetNestedType("MessageSourceFile", BindingFlags.NonPublic);
            Array inputs = Array.CreateInstance(fileType, files.Length);
            for (int index = 0; index < files.Length; ++index)
            {
                inputs.SetValue(
                    Activator.CreateInstance(
                        fileType,
                        InstanceMembers,
                        null,
                        new object[] { files[index].path, files[index].assetPath },
                        null
                    ),
                    index
                );
            }
            return Owner
                .GetMethod("BuildMessageSourceAssemblyIndex", StaticMembers)
                .Invoke(null, new object[] { inputs });
        }

        internal static bool IsComplete(object result) =>
            (bool)result.GetType().GetProperty("IsComplete", InstanceMembers).GetValue(result);

        internal static void InstallResult(string assemblyName, object result)
        {
            MethodInfo fromResult = typeof(Task)
                .GetMethod("FromResult")
                .MakeGenericMethod(result.GetType());
            InstallTask(assemblyName, (Task)fromResult.Invoke(null, new[] { result }));
        }

        private static void InstallTask(string assemblyName, Task task)
        {
            Type indexType = Owner.GetNestedType(
                "MessageSourceAssemblyIndex",
                BindingFlags.NonPublic
            );
            Indexes.Add(
                assemblyName,
                Activator.CreateInstance(
                    indexType,
                    InstanceMembers,
                    null,
                    new object[] { task },
                    null
                )
            );
        }

        internal sealed class PendingBuild : IDisposable
        {
            private readonly object _completion;
            private readonly object _result;
            private bool _completed;

            internal PendingBuild(string assemblyName)
            {
                _result = ReadFiles();
                Type completionType = typeof(TaskCompletionSource<>).MakeGenericType(
                    _result.GetType()
                );
                _completion = Activator.CreateInstance(completionType);
                InstallTask(
                    assemblyName,
                    (Task)completionType.GetProperty("Task").GetValue(_completion)
                );
            }

            internal void Complete()
            {
                if (_completed)
                {
                    return;
                }
                _completion.GetType().GetMethod("SetResult").Invoke(_completion, new[] { _result });
                _completed = true;
            }

            public void Dispose() => Complete();
        }
    }
}
#endif
