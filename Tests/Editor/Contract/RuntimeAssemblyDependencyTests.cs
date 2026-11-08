#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Contract
{
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Editor.Settings;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEditor.Compilation;

    [TestFixture]
    public sealed class RuntimeAssemblyDependencyTests
    {
        private static readonly AssembliesType[] CompilationKinds =
        {
            AssembliesType.Editor,
            AssembliesType.Player,
        };

        /// <summary>Assigns SDK sources to a known boundary without compiling them as Unity scripts.</summary>
        [Test]
        public void DotNetDevelopmentSourcesBelongToAnExcludedUnityAssembly()
        {
            const string sourceRoot = "Packages/com.wallstop-studios.dxmessaging/SourceGenerators";
            const string definition = sourceRoot + "/WallstopStudios.DxMessaging.DotNetOnly.asmdef";
            const string developmentAssembly = "WallstopStudios.DxMessaging.DotNetOnly";
            string[] scripts = AssetDatabase
                .FindAssets("t:MonoScript", new[] { sourceRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .ToArray();
            Assert.That(
                scripts,
                Is.Not.Empty,
                "The full checkout must expose its SDK sources to the ownership check."
            );
            foreach (string script in scripts)
            {
                Assert.That(
                    CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath(script),
                    Is.EqualTo(definition),
                    script
                        + ": SDK sources need an explicit owner to avoid Unity compilation warnings."
                );
            }
            foreach (AssembliesType kind in CompilationKinds)
            {
                string[] names = CompilationPipeline
                    .GetAssemblies(kind)
                    .Select(assembly => assembly.name)
                    .ToArray();
                CollectionAssert.Contains(
                    names,
                    "WallstopStudios.DxMessaging",
                    kind + ": retain the real runtime assembly."
                );
                CollectionAssert.DoesNotContain(
                    names,
                    developmentAssembly,
                    kind + ": build SDK projects with dotnet instead of Unity."
                );
            }
        }

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
