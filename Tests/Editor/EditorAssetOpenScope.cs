#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
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

#if UNITY_6000_6_OR_NEWER
        [OnOpenAsset(int.MinValue)]
        private static bool Observe(UnityEngine.EntityId entityId, int line)
        {
            EditorAssetOpenScope active = _active;
            if (active == null)
            {
                return false;
            }
            UnityEngine.Object opened = EditorUtility.EntityIdToObject(entityId);
            return active.Record(opened, line);
        }
#else
        [OnOpenAsset(int.MinValue)]
        private static bool Observe(int instanceId, int line)
        {
            EditorAssetOpenScope active = _active;
            if (active == null)
            {
                return false;
            }
            UnityEngine.Object opened = EditorUtility.InstanceIDToObject(instanceId);
            return active.Record(opened, line);
        }
#endif

        private bool Record(UnityEngine.Object opened, int line)
        {
            if (opened == null)
            {
                return false;
            }
            if (!string.Equals(AssetDatabase.GetAssetPath(opened), _scriptPath, StringComparison.Ordinal))
            {
                return false;
            }
            Calls++;
            Line = line;
            return true;
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
