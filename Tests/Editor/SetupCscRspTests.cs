#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using DxMessaging.Editor;
    using NUnit.Framework;

    [TestFixture]
    public sealed class SetupCscRspTests
    {
        private string _testRspFilePath;

        [SetUp]
        public void SetUp()
        {
            _testRspFilePath = Path.Combine(Path.GetTempPath(), $"test_csc_{Guid.NewGuid()}.rsp");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_testRspFilePath))
            {
                File.Delete(_testRspFilePath);
            }
        }

        public static IEnumerable<TestCaseData> CscRspCleanupCases()
        {
            yield return new TestCaseData(
                new[]
                {
                    @"-a:""Library/PackageCache/com.wallstop-studios.dxmessaging@4e74e1b2eec3/Editor/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll""",
                    @"-a:""Library/PackageCache/com.wallstop-studios.dxmessaging@4e74e1b2eec3/Editor/Analyzers/Microsoft.CodeAnalysis.dll""",
                    @"-a:""Library/PackageCache/com.wallstop-studios.dxmessaging@3d05efca60e4/Editor/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll""",
                    @"-r:""SomeOtherReference.dll""",
                },
                new[] { @"-r:""SomeOtherReference.dll""" }
            ).SetName("removes all DxMessaging analyzer and dependency -a entries");

            yield return new TestCaseData(
                new[]
                {
                    @"-a:""Library/PackageCache/some.other.package/Analyzers/OtherAnalyzer.dll""",
                    @"-r:""System.Runtime.dll""",
                    @"-define:SOMETHING",
                },
                new[]
                {
                    @"-a:""Library/PackageCache/some.other.package/Analyzers/OtherAnalyzer.dll""",
                    @"-r:""System.Runtime.dll""",
                    @"-define:SOMETHING",
                }
            ).SetName("preserves third-party analyzer and non-analyzer lines");

            yield return new TestCaseData(
                new[]
                {
                    @"-a:""Assets/Plugins/Editor/WallstopStudios.DxMessaging/WallstopStudios.DxMessaging.Analyzer.dll""",
                    @"-additionalfile:""Assets/DxMessaging.BaseCallIgnore.generated.txt""",
                },
                new[] { @"-additionalfile:""Assets/DxMessaging.BaseCallIgnore.generated.txt""" }
            ).SetName("preserves DxMessaging additionalfile while removing analyzer registration");
        }

        [TestCaseSource(nameof(CscRspCleanupCases))]
        public void RemovesDxMessagingAnalyzerEntriesAndPreservesEverythingElse(
            string[] inputLines,
            string[] expectedLines
        )
        {
            File.WriteAllLines(_testRspFilePath, inputLines);

            string[] cleaned = SetupCscRsp.CleanDxMessagingAnalyzerLines(
                File.ReadAllLines(_testRspFilePath),
                out bool foundStaleEntries
            );

            CollectionAssert.AreEqual(expectedLines, cleaned);
            Assert.AreEqual(inputLines.Length != expectedLines.Length, foundStaleEntries);
        }
    }
}
#endif
