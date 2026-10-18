// cspell:ignore Ldftn Ldfld Ldflda Ldsflda
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using DxMessaging.Editor.Analyzers;

    internal static class EditorProductionCalls
    {
        /// <summary>Uses the production IL decoder to inspect real call sites without replacing their targets.</summary>
        internal static IEnumerable<MethodBase> From(MethodInfo method) =>
            Members(method).OfType<MethodBase>();

        internal static IEnumerable<MemberInfo> Members(MethodInfo method)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type decoder = typeof(BaseCallIlInspector);
            OpCode[] singles = (OpCode[])decoder.GetField("s_singleByteOps", flags).GetValue(null);
            OpCode[] doubles = (OpCode[])decoder.GetField("s_twoByteOps", flags).GetValue(null);
            MethodInfo operandSize = decoder.GetMethod("GetOperandSize", flags);
            byte[] il = method.GetMethodBody().GetILAsByteArray();
            for (int index = 0; index < il.Length; )
            {
                byte first = il[index++];
                OpCode operation = first == 0xFE ? doubles[il[index++]] : singles[first];
                if (
                    operation == OpCodes.Call
                    || operation == OpCodes.Callvirt
                    || operation == OpCodes.Ldftn
                )
                {
                    yield return method.Module.ResolveMethod(
                        BitConverter.ToInt32(il, index),
                        method.DeclaringType.GetGenericArguments(),
                        method.GetGenericArguments()
                    );
                }
                if (
                    operation == OpCodes.Ldfld
                    || operation == OpCodes.Ldflda
                    || operation == OpCodes.Ldsfld
                    || operation == OpCodes.Ldsflda
                )
                {
                    yield return method.Module.ResolveField(
                        BitConverter.ToInt32(il, index),
                        method.DeclaringType.GetGenericArguments(),
                        method.GetGenericArguments()
                    );
                }
                index += (int)operandSize.Invoke(null, new object[] { operation, il, index });
            }
        }
    }
}
