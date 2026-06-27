#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// Minimal canonical ScriptableObject "event channel" carrying an <see cref="int"/>
    /// payload. Listeners register a delegate; <see cref="Raise"/> fans out to every
    /// listener in registration order. One asset per logical channel is the idiomatic
    /// ScriptableObject-architecture shape (for keyed dispatch the bridge creates K
    /// distinct channel instances). Instantiate via
    /// <see cref="ScriptableObject.CreateInstance{T}()"/> and destroy with
    /// <c>Object.DestroyImmediate</c>.
    /// </summary>
    public sealed class ScriptableObjectEventChannel : ScriptableObject
    {
        private readonly List<Action<int>> _listeners = new();

        public void Register(Action<int> listener)
        {
            _listeners.Add(listener);
        }

        public void Unregister(Action<int> listener)
        {
            _listeners.Remove(listener);
        }

        public void Raise(int payload)
        {
            for (int index = 0; index < _listeners.Count; index++)
            {
                _listeners[index](payload);
            }
        }
    }

    /// <summary>
    /// ScriptableObject event channel carrying a <see cref="ComparisonStructPayload"/> for the
    /// struct (no-boxing) scenario. Listeners receive the payload by value through a generic
    /// delegate, so no boxing occurs on the dispatch path.
    /// </summary>
    public sealed class ScriptableObjectStructEventChannel : ScriptableObject
    {
        private readonly List<Action<ComparisonStructPayload>> _listeners = new();

        public void Register(Action<ComparisonStructPayload> listener)
        {
            _listeners.Add(listener);
        }

        public void Unregister(Action<ComparisonStructPayload> listener)
        {
            _listeners.Remove(listener);
        }

        public void Raise(ComparisonStructPayload payload)
        {
            for (int index = 0; index < _listeners.Count; index++)
            {
                _listeners[index](payload);
            }
        }
    }
}
#endif
