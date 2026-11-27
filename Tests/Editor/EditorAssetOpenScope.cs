#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Reflection;
    using UnityEditor;
    using UnityEditor.Callbacks;

    /// <summary>Observes opening one owned script and prevents launching an external editor for that asset.</summary>
    internal sealed class EditorAssetOpenScope : IDisposable
    {
        private static EditorAssetOpenScope _active;
        private readonly string _scriptPath;

        internal EditorAssetOpenScope(MonoScript script)
        {
            if (script == null)
            {
                throw new ArgumentNullException(nameof(script));
            }
            if (_active != null)
            {
                throw new InvalidOperationException("A script-open observation is already owned.");
            }
            _scriptPath = AssetDatabase.GetAssetPath(script);
            if (string.IsNullOrEmpty(_scriptPath))
            {
                throw new InvalidOperationException("The observed MonoScript has no asset path.");
            }
            _active = this;
        }

        internal int Calls { get; private set; }
        internal int Line { get; private set; }

        [OnOpenAsset(int.MinValue)]
        private static bool Observe(int instanceId, int line)
        {
            EditorAssetOpenScope active = _active;
            if (active == null)
            {
                return false;
            }
            UnityEngine.Object opened = ResolveInstance(instanceId);
            if (opened == null)
            {
                return false;
            }
            if (
                !string.Equals(
                    AssetDatabase.GetAssetPath(opened),
                    active._scriptPath,
                    StringComparison.Ordinal
                )
            )
            {
                return false;
            }
            active.Calls++;
            active.Line = line;
            return true;
        }

        private static UnityEngine.Object ResolveInstance(int instanceId)
        {
#if UNITY_6000_6_OR_NEWER
            // Unity 6.6 makes the int accessor a compile error but still provides
            // it at runtime. Resolve it through reflection so this test observer
            // can compare the opened asset path without using an obsolete API.
            MethodInfo method = typeof(EditorUtility).GetMethod(
                "InstanceIDToObject",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(int) },
                null
            );
            return method == null
                ? null
                : (UnityEngine.Object)method.Invoke(null, new object[] { instanceId });
#else
            return EditorUtility.InstanceIDToObject(instanceId);
#endif
        }

        public void Dispose()
        {
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
        }
    }
}
#endif
