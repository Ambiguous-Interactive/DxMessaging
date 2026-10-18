namespace DxMessaging.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core.MessageBus;

    internal static partial class MessageBusInspection
    {
        private static readonly object ScopeGate = new object();
        private static RegistryScope _activeRegistryScope;
        private static PoolStorageScope _activePoolScope;

        internal static IDisposable IsolateIdleSweepRegistryForBenchmark() => new RegistryScope();

        internal static object IdleSweepRegistryIdentityForBenchmark =>
            ReadField(typeof(MessageBus), "IdleSweepBuses");

        internal static int IdleSweepRegistryCountForBenchmark =>
            ((ICollection)IdleSweepRegistryIdentityForBenchmark).Count;

        internal static IDisposable IsolateContextMapPoolForBenchmark() => new PoolStorageScope();

        internal static bool ContextMapPoolScopeActive => _activePoolScope != null;

        internal static ContextMapPoolBenchmarkObservation ObserveContextMapPoolForBenchmark()
        {
            object pool = ReadProperty(typeof(MessageBus), "ContextHandlerByTargetDicts");
            return new ContextMapPoolBenchmarkObservation(
                pool,
                ReadField(pool, "_lruQueue"),
                (bool)ReadProperty(pool, "UseLru"),
                (int)ReadProperty(pool, "MaxRetained")
            );
        }

        internal static void ConfigureContextMapPoolForBenchmark(bool useLru, int maxRetained)
        {
            object pool = ReadProperty(typeof(MessageBus), "ContextHandlerByTargetDicts");
            pool.GetType().GetProperty("UseLru").SetValue(pool, useLru);
            pool.GetType().GetProperty("MaxRetained").SetValue(pool, maxRetained);
        }

        private static void WriteField(object owner, string name, object value)
        {
            Type type = owner as Type ?? owner.GetType();
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, Members);
                if (field != null)
                {
                    field.SetValue(owner is Type ? null : owner, value);
                    return;
                }
            }
            throw new MissingFieldException(type.FullName, name);
        }

        /// <summary>Isolates contents of the production registry while retaining its identity.</summary>
        private sealed class RegistryScope : IDisposable
        {
            private readonly int _threadId = Environment.CurrentManagedThreadId;
            private readonly List<WeakReference<MessageBus>> _registry;
            private readonly WeakReference<MessageBus>[] _saved;
            private readonly int _savedCapacity;
            private readonly RegistryScope _parent;
            private bool _disposed;

            internal RegistryScope()
            {
                lock (ScopeGate)
                {
                    if (_activeRegistryScope != null && _activeRegistryScope._threadId != _threadId)
                    {
                        throw new InvalidOperationException(
                            "Nested idle-sweep benchmark scopes must share one owning thread."
                        );
                    }
                    _parent = _activeRegistryScope;
                    _registry =
                        (List<WeakReference<MessageBus>>)IdleSweepRegistryIdentityForBenchmark;
                    _saved = _registry.ToArray();
                    _savedCapacity = _registry.Capacity;
                    _registry.Clear();
                    _registry.Capacity = 0;
                    _activeRegistryScope = this;
                }
            }

            public void Dispose()
            {
                lock (ScopeGate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    if (Environment.CurrentManagedThreadId != _threadId)
                    {
                        throw new InvalidOperationException(
                            "Idle-sweep benchmark scopes must be disposed on their owning thread."
                        );
                    }
                    _disposed = true;
                    while (_activeRegistryScope != null && _activeRegistryScope._disposed)
                    {
                        RegistryScope completed = _activeRegistryScope;
                        completed._registry.Clear();
                        completed._registry.Capacity = completed._savedCapacity;
                        completed._registry.AddRange(completed._saved);
                        _activeRegistryScope = completed._parent;
                    }
                }
            }
        }

        /// <summary>
        /// Temporarily replaces cached collection storage inside the real singleton pool.
        /// Runtime always uses the same pool and its ordinary Rent/Return operations.
        /// </summary>
        private sealed class PoolStorageScope : IDisposable
        {
            private static readonly string[] StorageFields =
            {
                "_lruQueue",
                "_lruMembership",
                "_stack",
                "_stackMembership",
            };
            private static readonly string[] CounterFields =
            {
                "_hits",
                "_misses",
                "_evictions",
                "_useLru",
                "_maxRetained",
            };
            private readonly int _threadId = Environment.CurrentManagedThreadId;
            private readonly PoolStorageScope _parent;
            private readonly object _pool;
            private readonly Dictionary<string, object> _saved = new();
            private bool _disposed;

            internal PoolStorageScope()
            {
                lock (ScopeGate)
                {
                    if (_activePoolScope != null && _activePoolScope._threadId != _threadId)
                    {
                        throw new InvalidOperationException(
                            "Nested context-map benchmark scopes must share one owning thread."
                        );
                    }
                    _parent = _activePoolScope;
                    _pool = ReadProperty(typeof(MessageBus), "ContextHandlerByTargetDicts");
                    foreach (string name in StorageFields)
                    {
                        _saved.Add(name, ReadField(_pool, name));
                    }
                    foreach (string name in CounterFields)
                    {
                        _saved.Add(name, ReadField(_pool, name));
                    }
                    try
                    {
                        foreach (string name in StorageFields)
                        {
                            WriteField(
                                _pool,
                                name,
                                Activator.CreateInstance(_saved[name].GetType())
                            );
                        }
                        WriteField(_pool, "_hits", 0L);
                        WriteField(_pool, "_misses", 0L);
                        WriteField(_pool, "_evictions", 0L);
                    }
                    catch
                    {
                        RestoreFields();
                        throw;
                    }
                    _activePoolScope = this;
                }
            }

            private void RestoreFields()
            {
                foreach (KeyValuePair<string, object> entry in _saved)
                {
                    WriteField(_pool, entry.Key, entry.Value);
                }
            }

            public void Dispose()
            {
                lock (ScopeGate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    if (Environment.CurrentManagedThreadId != _threadId)
                    {
                        throw new InvalidOperationException(
                            "Context-map benchmark scopes must be disposed on their owning thread."
                        );
                    }
                    if (!ReferenceEquals(_activePoolScope, this))
                    {
                        throw new InvalidOperationException(
                            "Context-map benchmark scopes must be disposed in strict LIFO order."
                        );
                    }
                    bool useLru = (bool)ReadProperty(_pool, "UseLru");
                    int maxRetained = (int)ReadProperty(_pool, "MaxRetained");
                    _pool.GetType().GetMethod("Trim").Invoke(_pool, new object[] { 0 });
                    RestoreFields();
                    _activePoolScope = _parent;
                    _disposed = true;
                    ConfigureContextMapPoolForBenchmark(useLru, maxRetained);
                }
            }
        }
    }
}
