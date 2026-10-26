#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using UnityEditor;

    /// <summary>Observes and drains only callbacks queued by an owned production operation.</summary>
    internal sealed class EditorDelayCallScope : IDisposable
    {
        private readonly Type _owner;
        private readonly HashSet<Delegate> _baseline;
        private readonly List<EditorApplication.CallbackFunction> _history = new();
        private readonly List<EditorApplication.CallbackFunction> _suspendedBaseline = new();
        private bool _baselineRestored;

        internal EditorDelayCallScope(Type owner, bool suspendBaseline = false)
        {
            _owner = owner;
            Delegate[] baseline = Current();
            _baseline = new HashSet<Delegate>(baseline);
            if (suspendBaseline)
            {
                foreach (Delegate callback in baseline)
                {
                    if (BelongsTo(callback, _owner, new HashSet<object>(), 0))
                    {
                        _suspendedBaseline.Add((EditorApplication.CallbackFunction)callback);
                    }
                }
                foreach (EditorApplication.CallbackFunction callback in _suspendedBaseline)
                {
                    EditorApplication.delayCall -= callback;
                }
            }
        }

        private static Delegate[] Current() =>
            EditorApplication.delayCall?.GetInvocationList() ?? Array.Empty<Delegate>();

        private void Observe()
        {
            foreach (Delegate callback in Current())
            {
                if (
                    !_baseline.Contains(callback)
                    && !_history.Contains((EditorApplication.CallbackFunction)callback)
                    && BelongsTo(callback, _owner, new HashSet<object>(), 0)
                )
                {
                    _history.Add((EditorApplication.CallbackFunction)callback);
                }
            }
        }

        private static bool BelongsTo(
            Delegate callback,
            Type owner,
            HashSet<object> seen,
            int depth
        )
        {
            for (Type type = callback.Method.DeclaringType; type != null; type = type.DeclaringType)
            {
                if (type == owner)
                {
                    return true;
                }
            }
            object target = callback.Target;
            if (
                target == null
                || 8 < depth
                || !seen.Add(target)
                || !Attribute.IsDefined(target.GetType(), typeof(CompilerGeneratedAttribute))
            )
            {
                return false;
            }
            foreach (
                FieldInfo field in target
                    .GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            )
            {
                if (
                    field.GetValue(target) is Delegate nested
                    && BelongsTo(nested, owner, seen, depth + 1)
                )
                {
                    return true;
                }
            }
            return false;
        }

        internal int Count
        {
            get
            {
                Observe();
                return _history.Count;
            }
        }

        internal Action this[int index]
        {
            get
            {
                Observe();
                EditorApplication.CallbackFunction callback = _history[index];
                return () =>
                {
                    EditorApplication.delayCall -= callback;
                    callback();
                };
            }
        }

        internal void Clear()
        {
            Drain();
            _history.Clear();
        }

        private void Drain()
        {
            Observe();
            foreach (EditorApplication.CallbackFunction callback in _history)
            {
                EditorApplication.delayCall -= callback;
            }
        }

        public void Dispose()
        {
            Drain();
            if (_baselineRestored)
            {
                return;
            }
            foreach (EditorApplication.CallbackFunction callback in _suspendedBaseline)
            {
                EditorApplication.delayCall += callback;
            }
            _baselineRestored = true;
        }
    }

    /// <summary>Restores private static fixture state without a production reset hook.</summary>
    internal sealed class EditorStaticFieldScope : IDisposable
    {
        private readonly Dictionary<FieldInfo, object> _saved = new();

        internal EditorStaticFieldScope(Type owner, params string[] fields)
        {
            foreach (string name in fields)
            {
                FieldInfo field = owner.GetField(
                    name,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                );
                if (field == null)
                {
                    throw new MissingFieldException(owner.FullName, name);
                }
                _saved.Add(field, field.GetValue(null));
            }
        }

        internal void Set(string name, object value) =>
            _saved.Keys.Single(field => field.Name == name).SetValue(null, value);

        public void Dispose()
        {
            foreach (KeyValuePair<FieldInfo, object> saved in _saved)
            {
                saved.Key.SetValue(null, saved.Value);
            }
        }
    }
}
#endif
