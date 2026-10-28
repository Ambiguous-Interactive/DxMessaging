namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.MessageBus;

    internal static class SweepRegistryInspection
    {
        internal static IReadOnlyList<ObservedSweep> Rows
        {
            get
            {
                Func<MessageBus, bool, int>[] callbacks = (Func<MessageBus, bool, int>[])
                    MessageBusInspection.ReadField(
                        typeof(MessageBus),
                        "SweepableTypeCacheRegistry"
                    );
                List<ObservedSweep> rows = new();
                foreach (Func<MessageBus, bool, int> callback in callbacks)
                {
                    FieldInfo[] fields = StorageFields(callback.Method).ToArray();
                    if (fields.Length == 0)
                    {
                        fields = EditorProductionCalls
                            .From(callback.Method)
                            .OfType<MethodInfo>()
                            .Where(method => method.DeclaringType == typeof(MessageBus))
                            .SelectMany(StorageFields)
                            .Distinct()
                            .ToArray();
                    }
                    if (fields.Length != 1)
                    {
                        throw new InvalidOperationException(
                            $"A sweep callback must address one real message cache; observed {fields.Length} in {callback.Method}."
                        );
                    }
                    rows.Add(new ObservedSweep(fields[0], callback));
                }
                return rows;
            }
        }

        private static IEnumerable<FieldInfo> StorageFields(MethodInfo method) =>
            EditorProductionCalls
                .Members(method)
                .OfType<FieldInfo>()
                .Where(field =>
                {
                    Type type = field.FieldType.IsArray
                        ? field.FieldType.GetElementType()
                        : field.FieldType;
                    return field.DeclaringType == typeof(MessageBus)
                        && type.IsGenericType
                        && type.GetGenericTypeDefinition() == typeof(MessageCache<>);
                })
                .Distinct();
    }

    internal sealed class ObservedSweep
    {
        private readonly FieldInfo _field;
        private readonly Func<MessageBus, bool, int> _callback;

        internal ObservedSweep(FieldInfo field, Func<MessageBus, bool, int> callback)
        {
            _field = field;
            _callback = callback;
        }

        internal string StorageFieldName => _field.Name;

        internal int Sweep(MessageBus bus, bool force) => _callback(bus, force);
    }
}
