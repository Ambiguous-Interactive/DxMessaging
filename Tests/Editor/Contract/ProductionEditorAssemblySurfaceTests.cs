#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using DxMessaging.Editor;
    using DxMessaging.Tests.Runtime.Core;
    using NUnit.Framework;

    public sealed class ProductionEditorAssemblySurfaceTests
    {
        [Test]
        public void EditorAssemblyContainsNoTestOnlyEntryPoints()
        {
            Assert.That(
                ProductionAssemblySurfaceTests.FindTestOnlyEntryPoints(
                    typeof(DxMessagingEditorInitializer).Assembly
                ),
                Is.Empty
            );
        }

        [Test]
        public void EditorAssemblyContainsNoMutableStaticDelegateInjectionPoints()
        {
            string[] hooks = typeof(DxMessagingEditorInitializer)
                .Assembly.GetTypes()
                .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                .SelectMany(type =>
                    type.GetFields(
                            BindingFlags.Static
                                | BindingFlags.Public
                                | BindingFlags.NonPublic
                                | BindingFlags.DeclaredOnly
                        )
                        .Where(field =>
                            !field.IsInitOnly
                            && typeof(Delegate).IsAssignableFrom(field.FieldType)
                            && !type.GetEvents(
                                    BindingFlags.Static
                                        | BindingFlags.Public
                                        | BindingFlags.NonPublic
                                        | BindingFlags.DeclaredOnly
                                )
                                .Any(signal => signal.Name == field.Name)
                        )
                )
                .Select(field => field.DeclaringType.FullName + "." + field.Name)
                .ToArray();
            Assert.That(hooks, Is.Empty);
        }
    }
}
#endif
