namespace DxMessaging.Editor.Settings
{
#if UNITY_EDITOR
    using DxMessaging.Core.Configuration;
    using UnityEditor;
    using UnityEngine;

    internal static class DxMessagingRuntimeSettingsCreator
    {
        private const string ResourceFolder = "Assets/Resources";
        private const string ResourceAssetPath =
            ResourceFolder + "/" + DxMessagingRuntimeSettings.ResourceName + ".asset";

        [MenuItem("Assets/Create/Wallstop Studios/DxMessaging/Runtime Settings (in Resources)")]
        private static void CreateAssetInResources()
        {
            if (!AssetDatabase.IsValidFolder(ResourceFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
            }
            string targetPath = AssetDatabase.GenerateUniqueAssetPath(ResourceAssetPath);
            DxMessagingRuntimeSettings asset =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            AssetDatabase.CreateAsset(asset, targetPath);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(asset);
        }
    }
#endif
}
