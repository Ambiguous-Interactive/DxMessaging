#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Pooling;
    using NUnit.Framework;

    /// <summary>Reports bounded managed reference paths after a failed native collection window.</summary>
    /// <remarks>
    /// 2026-10-27: first/last array sampling missed an actually owned Untargeted callback at
    /// sparse type slot 80. Scan known MessageCache occupied ranges with a shared slot budget;
    /// report truncation when that budget or the per-array entry bound prevents full traversal.
    /// </remarks>
    internal static class NativeRetentionDiagnostics
    {
        private const int MaxNodes = 4096;
        private const int MaxDepth = 24;
        private const int DeepControlEdges = 20;
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
            object deepStrongControl = null;
            object deepWeakControl = null;
            List<(string Name, object Value)> sampled = new();
            try
            {
                deepStrongControl = CreateControlChain(target);
                deepWeakControl = CreateControlChain(new WeakReference(target));
                Result deepStrong = Find(
                    target,
                    new[] { ("strong-deep-control", deepStrongControl) }
                );
                Result deepWeak = Find(target, new[] { ("weak-deep-control", deepWeakControl) });
                Result strong = Find(target, new[] { ("strong-control", (object)strongControl) });
                Result weak = Find(target, new[] { ("weak-control", (object)weakControl) });
                if (
                    strong.Paths.Count != 1
                    || weak.Paths.Count != 0
                    || deepStrong.Paths.Count != 1
                    || deepWeak.Paths.Count != 0
                )
                {
                    TestContext.Out.WriteLine($"RETENTION {family}: INVALID observer controls.");
                    return;
                }
                Array.Clear(strongControl, 0, strongControl.Length);
                ClearControlChain(deepStrongControl);
                ClearControlChain(deepWeakControl);
                deepStrongControl = null;
                deepWeakControl = null;
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
                    $"RETENTION {family}: target={target.GetType().FullName}, controls=PASS, deepControlEdges={DeepControlEdges}, nodes={result.Nodes}, truncated={result.Truncated}, sparseArrays={result.SparseArrays}, sparseSlots={result.SparseSlotsScanned}, sparseValues={result.SparseValues}, paths={result.Paths.Count}. "
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
                ClearControlChain(deepStrongControl);
                ClearControlChain(deepWeakControl);
                deepStrongControl = null;
                deepWeakControl = null;
                Array.Clear(weakControl, 0, weakControl.Length);
                Array.Clear(roots, 0, roots.Length);
                sampled.Clear();
            }
        }

        private static object CreateControlChain(object leaf)
        {
            for (int index = 0; index < DeepControlEdges; ++index)
            {
                leaf = new ControlLink { Next = leaf };
            }
            return leaf;
        }

        private static void ClearControlChain(object root)
        {
            for (int index = 0; index < DeepControlEdges && root is ControlLink link; ++index)
            {
                root = link.Next;
                link.Next = null;
            }
        }

        private sealed class ControlLink
        {
            internal object Next;
        }

        private static Result Find(object target, IEnumerable<(string Name, object Value)> roots)
        {
            Result result = new();
            Queue<(object Value, string Path, int Depth, int SparseLength)> pending = new();
            HashSet<object> seen = new(new IdentityComparer());
            foreach ((string name, object value) in roots)
            {
                pending.Enqueue((value, name, 0, -1));
            }
            try
            {
                while (0 < pending.Count && result.Nodes < MaxNodes && result.Paths.Count < 8)
                {
                    (object value, string path, int depth, int sparseLength) = pending.Dequeue();
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
                        if (0 <= sparseLength)
                        {
                            ++result.SparseArrays;
                            int selected = 0;
                            int index = 0;
                            for (; index < sparseLength; ++index)
                            {
                                if (MaxNodes <= result.SparseSlotsScanned)
                                {
                                    break;
                                }
                                ++result.SparseSlotsScanned;
                                object child = array.GetValue(index);
                                if (child == null)
                                {
                                    continue;
                                }
                                if (MaxArrayItems <= selected || MaxNodes <= pending.Count)
                                {
                                    break;
                                }
                                pending.Enqueue((child, path + $"[{index}]", depth + 1, -1));
                                ++selected;
                                ++result.SparseValues;
                            }
                            result.Truncated |= index < sparseLength;
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
                                (array.GetValue(selected), path + $"[{selected}]", depth + 1, -1)
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
                                object child = field.GetValue(value);
                                int childSparseLength = -1;
                                if (
                                    field.Name == "_values"
                                    && type.IsGenericType
                                    && type.GetGenericTypeDefinition() == typeof(MessageCache<>)
                                    && child is Array values
                                )
                                {
                                    FieldInfo countField = type.GetField(
                                        "_count",
                                        BindingFlags.Instance | BindingFlags.NonPublic
                                    );
                                    childSparseLength = (int)countField.GetValue(value);
                                    if (childSparseLength < 0 || values.Length < childSparseLength)
                                    {
                                        throw new InvalidOperationException(
                                            "MessageCache occupied range exceeds its backing array."
                                        );
                                    }
                                }
                                pending.Enqueue(
                                    (child, path + "." + field.Name, depth + 1, childSparseLength)
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
            internal int SparseArrays;
            internal int SparseSlotsScanned;
            internal int SparseValues;
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
