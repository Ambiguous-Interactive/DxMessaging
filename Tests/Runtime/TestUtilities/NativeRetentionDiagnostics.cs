#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Pooling;
    using NUnit.Framework;

    /// <summary>Reports bounded managed reference paths after a failed native collection window.</summary>
    internal static class NativeRetentionDiagnostics
    {
        private const int MaxNodes = 4096;
        private const int MaxDepth = 12;
        private const int MaxArrayItems = 64;
        private static readonly HashSet<string> Reported = new();

        internal static void Report(string family, WeakReference reference, params object[] roots)
        {
            if (12 <= Reported.Count || !Reported.Add(family))
            {
                return;
            }
            object target = reference.Target;
            if (target == null)
            {
                return;
            }
            object[] strongControl = { target };
            object[] weakControl = { new WeakReference(target) };
            List<(string Name, object Value)> sampled = new();
            try
            {
                Result strong = Find(target, new[] { ("strong-control", (object)strongControl) });
                Result weak = Find(target, new[] { ("weak-control", (object)weakControl) });
                if (strong.Paths.Count != 1 || weak.Paths.Count != 0)
                {
                    TestContext.Out.WriteLine($"RETENTION {family}: INVALID observer controls.");
                    return;
                }
                Array.Clear(strongControl, 0, strongControl.Length);
                for (int index = 0; index < roots.Length; ++index)
                {
                    sampled.Add(($"owned[{index}]", roots[index]));
                }
                foreach (
                    Type type in new[]
                    {
                        typeof(MessageBus),
                        typeof(MessageHandler),
                        typeof(DxPools),
                    }
                )
                {
                    foreach (
                        FieldInfo field in type.GetFields(
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                        )
                    )
                    {
                        if (!field.IsLiteral && !field.FieldType.IsPointer)
                        {
                            sampled.Add(
                                ($"static {type.FullName}.{field.Name}", field.GetValue(null))
                            );
                        }
                    }
                }
                Result result = Find(target, sampled);
                TestContext.Out.WriteLine(
                    $"RETENTION {family}: target={target.GetType().FullName}, controls=PASS, nodes={result.Nodes}, truncated={result.Truncated}, paths={result.Paths.Count}. "
                        + (
                            result.Paths.Count == 0
                                ? "NO_PATH_IN_SAMPLED_ROOTS; this does not prove absence."
                                : string.Join("\n", result.Paths)
                        )
                );
            }
            catch (Exception exception)
            {
                TestContext.Out.WriteLine($"RETENTION {family}: INVALID observer {exception}");
            }
            finally
            {
                Array.Clear(strongControl, 0, strongControl.Length);
                Array.Clear(weakControl, 0, weakControl.Length);
                Array.Clear(roots, 0, roots.Length);
                sampled.Clear();
            }
        }

        private static Result Find(object target, IEnumerable<(string Name, object Value)> roots)
        {
            Result result = new();
            Queue<(object Value, string Path, int Depth)> pending = new();
            HashSet<object> seen = new(new IdentityComparer());
            foreach ((string name, object value) in roots)
            {
                pending.Enqueue((value, name, 0));
            }
            try
            {
                while (0 < pending.Count && result.Nodes < MaxNodes && result.Paths.Count < 8)
                {
                    (object value, string path, int depth) = pending.Dequeue();
                    if (value == null || IsLeaf(value) || !seen.Add(value))
                    {
                        continue;
                    }
                    ++result.Nodes;
                    if (ReferenceEquals(value, target))
                    {
                        result.Paths.Add(path);
                        continue;
                    }
                    if (MaxDepth <= depth)
                    {
                        result.Truncated = true;
                        continue;
                    }
                    if (value is Array array)
                    {
                        if (array.Rank != 1)
                        {
                            result.Truncated = true;
                            continue;
                        }
                        int count = Math.Min(array.Length, MaxArrayItems);
                        result.Truncated |= count != array.Length;
                        for (int index = 0; index < count; ++index)
                        {
                            if (MaxNodes <= pending.Count)
                            {
                                result.Truncated = true;
                                break;
                            }
                            int selected =
                                array.Length <= MaxArrayItems || index < MaxArrayItems / 2
                                    ? index
                                    : array.Length - count + index;
                            pending.Enqueue(
                                (array.GetValue(selected), path + $"[{selected}]", depth + 1)
                            );
                        }
                        continue;
                    }
                    for (Type type = value.GetType(); type != null; type = type.BaseType)
                    {
                        FieldInfo[] fields = type.GetFields(
                            BindingFlags.Instance
                                | BindingFlags.Public
                                | BindingFlags.NonPublic
                                | BindingFlags.DeclaredOnly
                        );
                        int count = Math.Min(fields.Length, MaxArrayItems);
                        result.Truncated |= count != fields.Length;
                        for (int index = 0; index < count; ++index)
                        {
                            if (MaxNodes <= pending.Count)
                            {
                                result.Truncated = true;
                                break;
                            }
                            FieldInfo field = fields[index];
                            if (!field.FieldType.IsPointer)
                            {
                                pending.Enqueue(
                                    (field.GetValue(value), path + "." + field.Name, depth + 1)
                                );
                            }
                        }
                    }
                }
                result.Truncated |= 0 < pending.Count;
                return result;
            }
            finally
            {
                pending.Clear();
                seen.Clear();
            }
        }

        private static bool IsLeaf(object value)
        {
            Type type = value.GetType();
            return type.IsPrimitive
                || type.IsEnum
                || value is string
                || value is decimal
                || value is IntPtr
                || value is UIntPtr
                || value is WeakReference
                || value is MemberInfo
                || value is Assembly
                || value is Module
                || (
                    type.IsGenericType
                    && (
                        type.GetGenericTypeDefinition() == typeof(WeakReference<>)
                        || type.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>)
                    )
                );
        }

        private sealed class Result
        {
            internal readonly List<string> Paths = new();
            internal int Nodes;
            internal bool Truncated;
        }

        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object left, object right) =>
                ReferenceEquals(left, right);

            int IEqualityComparer<object>.GetHashCode(object value) =>
                RuntimeHelpers.GetHashCode(value);
        }
    }
}
#endif
