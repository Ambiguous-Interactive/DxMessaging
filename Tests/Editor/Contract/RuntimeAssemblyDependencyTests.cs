#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Contract
{
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Editor.Settings;
    using NUnit.Framework;
    using UnityEditor;

    [TestFixture]
    public sealed class RuntimeAssemblyDependencyTests
    {
        [Test]
        public void RuntimeAssemblyDoesNotReferenceOptionalImguiModule()
        {
            string[] referencedAssemblies = typeof(DxMessagingRuntimeSettings)
                .Assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .ToArray();

            CollectionAssert.DoesNotContain(
                referencedAssemblies,
                "UnityEngine.IMGUIModule",
                "The runtime assembly must compile in stripped projects that omit the optional IMGUI module."
            );
        }

        [Test]
        public void RuntimeSettingsResourceMenuRemainsInEditorAssembly()
        {
            Assert.AreNotSame(
                typeof(DxMessagingRuntimeSettings).Assembly,
                typeof(DxMessagingRuntimeSettingsCreator).Assembly
            );
            MethodInfo createMethod = typeof(DxMessagingRuntimeSettingsCreator).GetMethod(
                "CreateAssetInResources",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(
                createMethod,
                "The Resources-folder creation command must remain available."
            );
            CustomAttributeData menuItem = createMethod
                .GetCustomAttributesData()
                .SingleOrDefault(attribute => attribute.AttributeType == typeof(MenuItem));
            Assert.IsNotNull(
                menuItem,
                "The Resources-folder creation command must remain a Unity menu item."
            );
            Assert.AreEqual(
                "Assets/Create/Wallstop Studios/DxMessaging/Runtime Settings (in Resources)",
                menuItem.ConstructorArguments[0].Value
            );
        }
    }
}
#endif
