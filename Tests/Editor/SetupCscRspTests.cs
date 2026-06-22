#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Settings;
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
                    @"/a:""Library/PackageCache/com.wallstop-studios.dxmessaging@4e74e1b2eec3/Editor/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll""",
                    @"/analyzer:""Assets/Plugins/Editor/WallstopStudios.DxMessaging/WallstopStudios.DxMessaging.Analyzer.dll""",
                    @"-a:""Library/PackageCache/com.wallstop-studios.dxmessaging@4e74e1b2eec3/Editor/Analyzers/Microsoft.CodeAnalysis.dll""",
                    @"-a:""Library/PackageCache/com.wallstop-studios.dxmessaging@3d05efca60e4/Editor/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll""",
                    @"-r:""SomeOtherReference.dll""",
                },
                new[] { @"-r:""SomeOtherReference.dll""" },
                true
            ).SetName("removes all DxMessaging analyzer and dependency -a entries");

            yield return new TestCaseData(
                new[]
                {
                    @"-a:""Library/PackageCache/some.other.package/Analyzers/OtherAnalyzer.dll""",
                    @"/analyzer:""Library/PackageCache/some.other.package/Analyzers/OtherAnalyzer.dll""",
                    @"-r:""System.Runtime.dll""",
                    @"-define:SOMETHING",
                },
                new[]
                {
                    @"-a:""Library/PackageCache/some.other.package/Analyzers/OtherAnalyzer.dll""",
                    @"/analyzer:""Library/PackageCache/some.other.package/Analyzers/OtherAnalyzer.dll""",
                    @"-r:""System.Runtime.dll""",
                    @"-define:SOMETHING",
                },
                false
            ).SetName("preserves third-party analyzer and non-analyzer lines");

            yield return new TestCaseData(
                new[]
                {
                    @"-a:""Assets/Plugins/Editor/WallstopStudios.DxMessaging/WallstopStudios.DxMessaging.Analyzer.dll""",
                    @"-additionalfile:""Assets/DxMessaging.BaseCallIgnore.generated.txt""",
                },
                new[] { @"-additionalfile:""Assets/DxMessaging.BaseCallIgnore.generated.txt""" },
                true
            ).SetName("preserves DxMessaging additionalfile while removing analyzer registration");

            yield return new TestCaseData(
                new[]
                {
                    @"/analyzer:""Assets/Plugins/Editor/WallstopStudios.DxMessaging/WallstopStudios.DxMessaging.Analyzer.dll"" -define:FOO",
                    @"""/a:Library/PackageCache/com.wallstop-studios.dxmessaging/Editor/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll"" -r:""Other.dll""",
                },
                new[] { "-define:FOO", @"-r:""Other.dll""" },
                true
            ).SetName("removes only DxMessaging analyzer tokens from mixed response-file lines");

            yield return new TestCaseData(
                new[]
                {
                    @"# /analyzer:""Assets/Plugins/Editor/WallstopStudios.DxMessaging/WallstopStudios.DxMessaging.Analyzer.dll""",
                },
                new[]
                {
                    @"# /analyzer:""Assets/Plugins/Editor/WallstopStudios.DxMessaging/WallstopStudios.DxMessaging.Analyzer.dll""",
                },
                false
            ).SetName("preserves commented-out DxMessaging analyzer entries");
        }

        [TestCaseSource(nameof(CscRspCleanupCases))]
        public void RemovesDxMessagingAnalyzerEntriesAndPreservesEverythingElse(
            string[] inputLines,
            string[] expectedLines,
            bool expectedFoundStaleEntries
        )
        {
            File.WriteAllLines(_testRspFilePath, inputLines);

            string[] cleaned = SetupCscRsp.CleanDxMessagingAnalyzerLines(
                File.ReadAllLines(_testRspFilePath),
                out bool foundStaleEntries
            );

            CollectionAssert.AreEqual(expectedLines, cleaned);
            Assert.AreEqual(expectedFoundStaleEntries, foundStaleEntries);
        }

        public static IEnumerable<TestCaseData> AdditionalFileSyncCases()
        {
            string sidecarPath = DxMessagingBaseCallIgnoreSync.SidecarAssetPath;
            string desiredLine = $@"-additionalfile:""{sidecarPath}""";

            yield return new TestCaseData(
                new[] { @"-r:""System.Runtime.dll""" },
                true,
                new[] { @"-r:""System.Runtime.dll""", desiredLine },
                true
            ).SetName("appends missing sidecar entry after deferred sidecar generation");

            yield return new TestCaseData(
                new[] { desiredLine },
                true,
                new[] { desiredLine },
                false
            ).SetName("keeps existing canonical sidecar entry");

            yield return new TestCaseData(
                new[] { $@"# -additionalfile:""{sidecarPath}""" },
                true,
                new[] { $@"# -additionalfile:""{sidecarPath}""", desiredLine },
                true
            ).SetName("does not treat commented sidecar entry as active wiring");

            yield return new TestCaseData(
                new[]
                {
                    @"-additionalfile:Assets/Editor/DxMessaging.BaseCallIgnore.txt",
                    @"/additionalfile:""Assets/Editor/DxMessaging.BaseCallIgnore.txt""",
                    @"""/additionalfile:Assets/Editor/DxMessaging.BaseCallIgnore.txt""",
                    desiredLine,
                    desiredLine,
                },
                true,
                new[] { desiredLine },
                true
            ).SetName("canonicalizes and deduplicates dash and slash sidecar entries");

            yield return new TestCaseData(
                new[]
                {
                    @"/additionalfile:""Assets/DxMessaging.BaseCallIgnore.generated.txt""",
                    @"-r:""System.Runtime.dll""",
                },
                true,
                new[] { @"-r:""System.Runtime.dll""", desiredLine },
                true
            ).SetName("replaces stale slash-prefixed moved sidecar path");

            yield return new TestCaseData(
                new[]
                {
                    @"""/additionalfile:Assets/Editor/DxMessaging.BaseCallIgnore.txt"" -define:FOO",
                    @"-r:""System.Runtime.dll""",
                },
                true,
                new[] { $"{desiredLine} -define:FOO", @"-r:""System.Runtime.dll""" },
                true
            ).SetName("canonicalizes whole-quoted sidecar token without dropping mixed options");

            yield return new TestCaseData(
                new[]
                {
                    @"""/additionalfile:Assets/DxMessaging.BaseCallIgnore.generated.txt"" -define:FOO",
                    @"-r:""System.Runtime.dll""",
                },
                true,
                new[] { "-define:FOO", @"-r:""System.Runtime.dll""", desiredLine },
                true
            ).SetName("removes whole-quoted stale sidecar token without dropping mixed options");

            yield return new TestCaseData(
                new[] { desiredLine, @"-r:""System.Runtime.dll""" },
                false,
                new[] { @"-r:""System.Runtime.dll""" },
                true
            ).SetName("removes sidecar entry when sidecar is absent");

            yield return new TestCaseData(
                new[] { @"-r:""System.Runtime.dll""" },
                false,
                new[] { @"-r:""System.Runtime.dll""" },
                false
            ).SetName("does not modify unrelated lines when sidecar is absent");
        }

        [TestCaseSource(nameof(AdditionalFileSyncCases))]
        public void SynchronizesBaseCallIgnoreAdditionalFileEntry(
            string[] inputLines,
            bool sidecarExists,
            string[] expectedLines,
            bool expectedModified
        )
        {
            string[] synchronized = SetupCscRsp.SynchronizeAdditionalFileForIgnoreListLines(
                inputLines,
                DxMessagingBaseCallIgnoreSync.SidecarAssetPath,
                sidecarExists,
                out bool modified
            );

            CollectionAssert.AreEqual(expectedLines, synchronized);
            Assert.AreEqual(expectedModified, modified);
        }

        public static IEnumerable<TestCaseData> LegacyAnalyzerCopyRemovalCases()
        {
            yield return new TestCaseData(
                new[]
                {
                    "WallstopStudios.DxMessaging.SourceGenerators.dll",
                    "WallstopStudios.DxMessaging.SourceGenerators.dll.meta",
                    "WallstopStudios.DxMessaging.Analyzer.dll",
                    "WallstopStudios.DxMessaging.Analyzer.dll.meta",
                    "Microsoft.CodeAnalysis.dll",
                    "Microsoft.CodeAnalysis.dll.meta",
                },
                true
            ).SetName("removes a folder of only analyzer DLLs and their meta sidecars");

            yield return new TestCaseData(
                new[]
                {
                    "WallstopStudios.DxMessaging.Analyzer.DLL",
                    "WallstopStudios.DxMessaging.Analyzer.DLL.META",
                },
                true
            ).SetName("treats the dll and dll.meta suffixes case-insensitively");

            yield return new TestCaseData(
                new[]
                {
                    "WallstopStudios.DxMessaging.SourceGenerators.dll",
                    "WallstopStudios.DxMessaging.SourceGenerators.dll.meta",
                    "ConsumerNotes.cs",
                },
                false
            ).SetName("preserves the folder when a consumer added a foreign file");

            yield return new TestCaseData(
                new[] { "WallstopStudios.DxMessaging.Analyzer.dll", "NestedFolder" },
                false
            ).SetName("preserves the folder when an entry is neither a .dll nor a .dll.meta");

            yield return new TestCaseData(Array.Empty<string>(), false).SetName(
                "does nothing for an empty folder"
            );

            yield return new TestCaseData(new[] { "orphan.dll.meta" }, false).SetName(
                "does nothing when only orphaned meta sidecars remain (no analyzer dll)"
            );
        }

        [TestCaseSource(nameof(LegacyAnalyzerCopyRemovalCases))]
        public void RecognizesWhenLegacyAnalyzerCopyIsSafeToRemove(
            string[] folderEntries,
            bool expectedSafeToRemove
        )
        {
            Assert.AreEqual(
                expectedSafeToRemove,
                SetupCscRsp.IsLegacyAnalyzerCopySafeToRemove(folderEntries)
            );
        }
    }
}
#endif
