#nullable enable annotations
namespace DxMessaging.Editor.Analyzers
{
    using System;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Security;

    /// <summary>
    /// Pure (Unity-API-free) IL inspector that decides whether a given <see cref="MethodInfo"/>'s
    /// IL body invokes a parent's same-named method (i.e. the IL emit shape of <c>base.X()</c>).
    /// Extracted from <c>BaseCallTypeScanner</c> so the dotnet-test project can cover the byte
    /// walker without depending on Unity APIs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why does this exist?</b> The console-scrape harvester is non-deterministic across Unity
    /// 2021 cache hits (Unity skips routing analyzer warnings to <c>LogEntries</c> /
    /// <c>CompilerMessage[]</c> on incremental compiles where Bee/csc reused a cached output).
    /// IL reflection over the loaded assemblies in the AppDomain is deterministic -- the bytes do
    /// not depend on Unity's compile-pipeline state. <see cref="BaseCallTypeScanner"/> uses this
    /// helper to classify every loaded <c>MessageAwareComponent</c> subclass on every domain
    /// reload.
    /// </para>
    /// <para>
    /// <b>Proper OpCodes-table walker.</b> The walker decodes every CIL instruction by looking up
    /// its <see cref="OpCode"/> in the static tables built from <see cref="OpCodes"/> reflection
    /// (single-byte and two-byte 0xFE-prefix forms) and steps the operand-size that the opcode
    /// declares (<see cref="OpCode.OperandType"/>). Misalignment past multi-byte-operand opcodes
    /// (<c>switch</c> jump tables, <c>ldstr</c> 4-byte tokens, 8-byte literal constants, etc.) is
    /// therefore impossible -- the walker either consumes every byte correctly or stops at the
    /// first unrecognised opcode. Phantom DXMSG006 from a misread <c>0x28</c> inside a wider
    /// operand is no longer a failure mode.
    /// </para>
    /// <para>
    /// <b>Defensive bias.</b> When we cannot reason at all (null method, empty name, inaccessible
    /// IL body, <c>GetMethodBody()</c> returns null on abstract / P/Invoke / IL2CPP-stripped
    /// targets, or a documented reflection/metadata access failure), the inspector returns <c>true</c>
    /// ("assume clean -- calls base") so the scanner never invents a phantom warning. The
    /// compile-time analyzer is the authoritative source for CI builds (DXMSG006/007/009/010 via
    /// full Roslyn semantic-model precision); the IL scanner exists only to make the editor
    /// overlay light up at edit-time, where a missed warning is far worse than a phantom one.
    /// </para>
    /// </remarks>
#if UNITY_EDITOR
    public
#else
    internal
#endif
    static class BaseCallIlInspector
    {
        // CIL opcode tables, indexed by the low byte of OpCode.Value. Built once by reflecting over
        // System.Reflection.Emit.OpCodes; every public static OpCode field there represents a
        // canonical CIL instruction. The two-byte form of the table is used when a 0xFE prefix is
        // observed in the IL stream; otherwise we use the single-byte form. Because CIL specifies
        // exactly two prefix bytes (single-byte = direct, two-byte = 0xFE prefix), this division
        // covers every defined opcode.
        private static readonly OpCode[] s_singleByteOps = BuildOpCodeTable(twoByte: false);
        private static readonly OpCode[] s_twoByteOps = BuildOpCodeTable(twoByte: true);

        private static OpCode[] BuildOpCodeTable(bool twoByte)
        {
            OpCode[] table = new OpCode[256];
            foreach (
                FieldInfo field in typeof(OpCodes).GetFields(
                    BindingFlags.Public | BindingFlags.Static
                )
            )
            {
                if (field.GetValue(null) is not OpCode op)
                {
                    continue;
                }
                ushort value = (ushort)op.Value;
                bool isTwoByte = (value & 0xFF00) == 0xFE00;
                bool isSingleByte = (value & 0xFF00) == 0;
                if (twoByte && isTwoByte)
                {
                    table[value & 0xFF] = op;
                }
                else if (!twoByte && isSingleByte)
                {
                    table[value & 0xFF] = op;
                }
            }
            return table;
        }

        /// <summary>
        /// Returns <c>true</c> if <paramref name="method"/>'s IL body contains a
        /// <c>call</c>/<c>callvirt</c> to a parent type's same-named method. Defensive:
        /// returns <c>true</c> (assume-clean) if the IL body is null/inaccessible to avoid
        /// false-positive warnings on platforms or methods where reflection on bodies is
        /// restricted (abstract methods, P/Invoke, IL2CPP-stripped bodies, etc.).
        /// </summary>
        /// <param name="method">The override on the descendant type whose IL we wish to inspect.</param>
        /// <param name="methodName">The expected base method name (e.g. <c>"OnEnable"</c>).</param>
        /// <returns>
        /// <c>true</c> if the IL contains a base-call shape, OR the IL was inaccessible (safe
        /// default -- assume clean). <c>false</c> only when IL was readable AND no call/callvirt
        /// targeting a parent same-named method was found.
        /// </returns>
        public static bool MethodIlContainsBaseCall(MethodInfo? method, string? methodName)
        {
            if (method == null || string.IsNullOrEmpty(methodName))
            {
                // Defensive: treat as clean when we don't have enough information to reason. This
                // ensures the scanner never emits a phantom warning on a degenerate input.
                return true;
            }

            try
            {
                MethodBody? body = method.GetMethodBody();
                if (body == null)
                {
                    // Abstract / extern / runtime-implemented / IL2CPP-stripped; cannot inspect.
                    return true;
                }

                byte[]? il = body.GetILAsByteArray();
                if (il == null || il.Length == 0)
                {
                    return true;
                }

                Module module = method.Module;
                Type[]? genericTypeArgs =
                    method.DeclaringType?.IsGenericType == true
                        ? method.DeclaringType.GetGenericArguments()
                        : null;
                Type[]? genericMethodArgs = method.IsGenericMethod
                    ? method.GetGenericArguments()
                    : null;

                int i = 0;
                while (i < il.Length)
                {
                    OpCode op;
                    if (il[i] == 0xFE)
                    {
                        // Two-byte (0xFE-prefixed) opcode. Without a following byte we cannot
                        // decode the instruction; bail out conservatively. Truncated IL is not a
                        // shape Roslyn ever emits, so reaching this path means we mis-stepped and
                        // the safest answer is the assume-clean default.
                        if (i + 1 >= il.Length)
                        {
                            return true;
                        }
                        op = s_twoByteOps[il[i + 1]];
                        i += 2;
                    }
                    else
                    {
                        op = s_singleByteOps[il[i]];
                        i += 1;
                    }

                    // Unrecognised opcode (zero-initialised slot in the table); abandon the walk
                    // rather than risk the rest of the stream getting misread. Returning the
                    // assume-clean default keeps the scanner from inventing a phantom warning.
                    if (op.Size == 0)
                    {
                        return true;
                    }

                    if (op == OpCodes.Call || op == OpCodes.Callvirt)
                    {
                        if (i + 4 > il.Length)
                        {
                            return true;
                        }
                        int token = BitConverter.ToInt32(il, i);
                        if (
                            TryResolveMethod(
                                module,
                                token,
                                genericTypeArgs,
                                genericMethodArgs,
                                out MethodBase? target
                            )
                        )
                        {
                            if (
                                target != null
                                && string.Equals(target.Name, methodName, StringComparison.Ordinal)
                            )
                            {
                                Type? declaring = method.DeclaringType;
                                Type? resolved = target.DeclaringType;
                                // Guard against false-positives: the resolved method must live on a
                                // STRICT base type of the declaring class (not the declaring class
                                // itself, not a sibling, not a generic-arg shadow). IsAssignableFrom
                                // checks "is `declaring` assignable TO `resolved`"; i.e. is
                                // `resolved` an ancestor of `declaring`.
                                if (
                                    declaring != null
                                    && resolved != null
                                    && declaring != resolved
                                    && resolved.IsAssignableFrom(declaring)
                                )
                                {
                                    return true;
                                }
                            }
                        }
                        i += 4;
                        continue;
                    }

                    // Step over the operand based on the opcode's declared operand type. Every
                    // CIL operand size is decided by OperandType, which is exactly why the table
                    // walker is misalignment-proof.
                    i += GetOperandSize(op, il, i);
                }
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (BadImageFormatException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (NotSupportedException)
            {
                return true;
            }
            catch (SecurityException)
            {
                return true;
            }
        }

        private static bool TryResolveMethod(
            Module module,
            int token,
            Type[]? genericTypeArgs,
            Type[]? genericMethodArgs,
            out MethodBase? target
        )
        {
            try
            {
                target = module.ResolveMethod(token, genericTypeArgs, genericMethodArgs);
                return true;
            }
            catch (ArgumentException)
            {
                target = null;
                return false;
            }
            catch (BadImageFormatException)
            {
                target = null;
                return false;
            }
            catch (InvalidOperationException)
            {
                target = null;
                return false;
            }
            catch (NotSupportedException)
            {
                target = null;
                return false;
            }
            catch (SecurityException)
            {
                target = null;
                return false;
            }
        }

        // Returns the number of operand bytes that follow an opcode of the given OperandType,
        // given the operand-start offset (needed for InlineSwitch's variable-length jump table).
        private static int GetOperandSize(OpCode op, byte[] il, int operandStart)
        {
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    // 4-byte case count, then N × 4-byte branch targets. Truncated stream → bail
                    // by consuming the rest defensively (the outer loop's bounds check then ends
                    // the walk).
                    if (operandStart + 4 > il.Length)
                    {
                        return il.Length - operandStart;
                    }
                    int caseCount = BitConverter.ToInt32(il, operandStart);
                    if (caseCount < 0)
                    {
                        // Negative case-count is malformed IL; bail conservatively.
                        return il.Length - operandStart;
                    }
                    return 4 + caseCount * 4;
                default:
                    // Unknown OperandType; bail conservatively by consuming the rest of the
                    // stream so the outer loop terminates without misaligning further.
                    return il.Length - operandStart;
            }
        }
    }
}
