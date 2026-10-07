namespace DxMessaging.Editor.Settings
{
#if UNITY_EDITOR
    using UnityEditor;

    // Invalidate after import without loading settings or mutating assets in the callback.
    internal sealed class DxMessagingSettingsAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths
        )
        {
            DxMessagingSettings.NotifyAssetsChanged(
                importedAssets,
                deletedAssets,
                movedAssets,
                movedFromAssetPaths
            );
        }
    }
#endif
}
