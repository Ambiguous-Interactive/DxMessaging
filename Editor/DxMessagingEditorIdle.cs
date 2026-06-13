namespace DxMessaging.Editor
{
#if UNITY_EDITOR
    using System;
    using UnityEditor;

    /// <summary>
    /// Shared guard for editor callbacks that mutate Unity's AssetDatabase.
    /// </summary>
    internal static class DxMessagingEditorIdle
    {
        internal static bool CanMutateAssetDatabase()
        {
            return !EditorApplication.isUpdating && !EditorApplication.isCompiling;
        }

        internal static void ScheduleAssetDatabaseMutation(Action work)
        {
            ScheduleAssetDatabaseMutation(work, ScheduleDelayCall, CanMutateAssetDatabase);
        }

        internal static void ScheduleAssetDatabaseMutation(
            Action work,
            Action<Action> scheduler,
            Func<bool> canMutateAssetDatabase
        )
        {
            if (work == null)
            {
                return;
            }

            Action<Action> effectiveScheduler = scheduler ?? ScheduleDelayCall;
            Func<bool> effectiveCanMutate = canMutateAssetDatabase ?? CanMutateAssetDatabase;

            effectiveScheduler(() =>
                RunAssetDatabaseMutationWhenIdle(work, effectiveScheduler, effectiveCanMutate)
            );
        }

        private static void RunAssetDatabaseMutationWhenIdle(
            Action work,
            Action<Action> scheduler,
            Func<bool> canMutateAssetDatabase
        )
        {
            if (!canMutateAssetDatabase())
            {
                ScheduleAssetDatabaseMutation(work, scheduler, canMutateAssetDatabase);
                return;
            }

            work();
        }

        private static void ScheduleDelayCall(Action work)
        {
            EditorApplication.delayCall += () => work();
        }
    }
#endif
}
