#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Core.Pooling
{
    using System;
    using MessageBus;
    using UnityEngine;
    using UnityEngine.LowLevel;
    using UnityEngine.PlayerLoop;

    /// <summary>
    /// Unity PlayerLoop bridge that gives idle eviction a cadence even when no
    /// messages are emitted.
    /// </summary>
    internal static class EvictionPlayerLoopHook
    {
        private static readonly Type HookType = typeof(EvictionPlayerLoopHook);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Install()
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (InstallInto(ref root))
            {
                PlayerLoop.SetPlayerLoop(root);
            }
        }

        internal static bool InstallInto(ref PlayerLoopSystem root)
        {
            if (ContainsHook(root))
            {
                return false;
            }

            PlayerLoopSystem hook = new PlayerLoopSystem
            {
                type = HookType,
                updateDelegate = SweepIdleBuses,
            };

            return InsertUnder<Update>(ref root, hook);
        }

        private static void SweepIdleBuses()
        {
            MessageBus.SweepIdleBusesFromPlayerLoop();
        }

        internal static bool ContainsHook(PlayerLoopSystem system)
        {
            if (system.type == HookType)
            {
                return true;
            }

            PlayerLoopSystem[] subsystems = system.subSystemList;
            if (subsystems == null)
            {
                return false;
            }

            for (int i = 0; i < subsystems.Length; ++i)
            {
                if (ContainsHook(subsystems[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool InsertUnder<TTarget>(ref PlayerLoopSystem system, PlayerLoopSystem hook)
        {
            if (system.type == typeof(TTarget))
            {
                PlayerLoopSystem[] oldList = system.subSystemList
                    ?? Array.Empty<PlayerLoopSystem>();
                PlayerLoopSystem[] newList = new PlayerLoopSystem[oldList.Length + 1];
                Array.Copy(oldList, newList, oldList.Length);
                newList[oldList.Length] = hook;
                system.subSystemList = newList;
                return true;
            }

            PlayerLoopSystem[] subsystems = system.subSystemList;
            if (subsystems == null)
            {
                return false;
            }

            for (int i = 0; i < subsystems.Length; ++i)
            {
                PlayerLoopSystem child = subsystems[i];
                if (InsertUnder<TTarget>(ref child, hook))
                {
                    subsystems[i] = child;
                    return true;
                }
            }

            return false;
        }
    }
}
#endif
