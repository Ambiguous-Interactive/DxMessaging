#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using DxMessaging.Core;
    using UnityEditor;
    using UnityEditor.Callbacks;

    /// <summary>Observes opening one owned script and prevents launching an external editor for that asset.</summary>
    internal sealed class EditorAssetOpenScope : IDisposable
    {
        private static EditorAssetOpenScope _active;
        private readonly int _scriptId;

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
            _scriptId = InstanceId.StableId(script);
            _active = this;
        }

        internal int Calls { get; private set; }
        internal int Line { get; private set; }

        [OnOpenAsset(int.MinValue)]
        private static bool Observe(int instanceId, int line)
        {
            EditorAssetOpenScope active = _active;
            if (active == null || instanceId != active._scriptId)
            {
                return false;
            }
            active.Calls++;
            active.Line = line;
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
