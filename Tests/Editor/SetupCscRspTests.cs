#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Settings;
    using NUnit.Framework;
    using UnityEngine;

    [TestFixture]
    public sealed class SetupCscRspTests
    {
        private string _testRspFilePath;
        private string _projectDirectory;
        private string _assetsDirectory;

        private const string SidecarArgument =
            "-additionalfile:\"Assets/Editor/DxMessaging.BaseCallIgnore.txt\"";
        private const string LegacyAnalyzerArgument =
            "-a:\"Assets/Plugins/Editor/WallstopStudios.DxMessaging/WallstopStudios.DxMessaging.Analyzer.dll\"";

        [SetUp]
        public void SetUp()
        {
            _projectDirectory = Path.Combine(Path.GetTempPath(), $"dxm_rsp_{Guid.NewGuid():N}");
            _assetsDirectory = Path.Combine(_projectDirectory, "Assets");
            Directory.CreateDirectory(_assetsDirectory);
            _testRspFilePath = Path.Combine(_assetsDirectory, "csc.rsp");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_projectDirectory))
            {
                Directory.Delete(_projectDirectory, true);
            }
        }

        [Test]
        public void ResponseFilePathIsUnderAssetsWhereUnityReadsIt()
        {
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(Application.dataPath, "csc.rsp")),
                Path.GetFullPath(SetupCscRsp.GetRspFilePath(Application.dataPath)),
                "Unity reads the response file from Assets/csc.rsp, not the project root."
            );
        }

        public static IEnumerable<TestCaseData> ResponseFileMigrationCases()
        {
            yield return new TestCaseData(
                null,
                null,
                true,
                new[] { SidecarArgument },
                null,
                1
            ).SetName("creates the response file under Assets when the sidecar exists");
            yield return new TestCaseData(null, null, false, null, null, 0).SetName(
                "does not create an empty response file before sidecar generation"
            );
            yield return new TestCaseData(
                new[] { "# consumer asset options", "-define:ASSET", LegacyAnalyzerArgument },
                new[] { LegacyAnalyzerArgument + " -define:ROOT", SidecarArgument },
                true,
                new[] { "# consumer asset options", "-define:ASSET", SidecarArgument },
                new[] { "-define:ROOT" },
                1
            ).SetName("preserves consumer options in each location while migrating managed wiring");
            yield return new TestCaseData(
                null,
                new[] { SidecarArgument, LegacyAnalyzerArgument },
                true,
                new[] { SidecarArgument },
                null,
                1
            ).SetName("deletes a package-only root response file after migration");
            yield return new TestCaseData(
                null,
                new[] { "  # root options", "  -define:ROOT", "", "-r:Other.dll" },
                false,
                null,
                new[] { "  # root options", "  -define:ROOT", "", "-r:Other.dll" },
                0
            ).SetName("leaves unrelated root response-file content untouched and inactive");
            yield return new TestCaseData(
                null,
                Array.Empty<string>(),
                false,
                null,
                Array.Empty<string>(),
                0
            ).SetName("does not delete an empty consumer root response file");
            yield return new TestCaseData(
                new[] { SidecarArgument, LegacyAnalyzerArgument },
                new[] { SidecarArgument, LegacyAnalyzerArgument },
                false,
                Array.Empty<string>(),
                null,
                1
            ).SetName(
                "removes stale managed options from both locations when the sidecar is missing"
            );
            yield return new TestCaseData(
                new[] { "-r:Other.dll", SidecarArgument },
                new[] { "# consumer root comment", SidecarArgument },
                true,
                new[] { "-r:Other.dll", SidecarArgument },
                new[] { "# consumer root comment" },
                0
            ).SetName("retains root comments and skips importing an already-correct asset");
        }

        [TestCaseSource(nameof(ResponseFileMigrationCases))]
        public void SynchronizesAssetAndLegacyResponseFilesWithoutRepeatedImports(
            string[] assetLines,
            string[] rootLines,
            bool sidecarExists,
            string[] expectedAssetLines,
            string[] expectedRootLines,
            int expectedImports
        )
        {
            string rootRspPath = Path.Combine(_projectDirectory, "csc.rsp");
            if (assetLines != null)
            {
                File.WriteAllLines(_testRspFilePath, assetLines);
            }
            if (rootLines != null)
            {
                File.WriteAllLines(rootRspPath, rootLines);
            }
            if (sidecarExists)
            {
                CreateSidecar();
            }

            string context =
                $"asset=[{string.Join(",", assetLines ?? Array.Empty<string>())}], "
                + $"root=[{string.Join(",", rootLines ?? Array.Empty<string>())}], "
                + $"sidecarExists={sidecarExists}, expectedImports={expectedImports}";
            List<string> imports = new();
            bool importPending = false;
            SetupCscRsp.SynchronizeResponseFiles(_assetsDirectory, imports.Add, ref importPending);

            AssertResponseFile(_testRspFilePath, expectedAssetLines, context);
            AssertResponseFile(rootRspPath, expectedRootLines, context);
            CollectionAssert.AreEqual(
                expectedImports == 0 ? Array.Empty<string>() : new[] { "Assets/csc.rsp" },
                imports,
                $"Only the Unity asset path may be imported once per update: {context}."
            );
            Assert.IsFalse(
                importPending,
                $"Successful imports must clear pending state: {context}."
            );

            DateTime unchangedTime = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            foreach (string path in new[] { _testRspFilePath, rootRspPath })
            {
                if (File.Exists(path))
                {
                    File.SetLastWriteTimeUtc(path, unchangedTime);
                }
            }
            SetupCscRsp.SynchronizeResponseFiles(_assetsDirectory, imports.Add, ref importPending);
            Assert.AreEqual(
                expectedImports,
                imports.Count,
                $"An unchanged sync must not reimport: {context}."
            );
            foreach (string path in new[] { _testRspFilePath, rootRspPath })
            {
                if (File.Exists(path))
                {
                    Assert.AreEqual(
                        unchangedTime,
                        File.GetLastWriteTimeUtc(path),
                        $"An unchanged sync must not rewrite {path}: {context}."
                    );
                }
            }
        }

        [Test]
        public void FailedAssetWritePreservesLegacyFileAndCanBeRetried()
        {
            CreateSidecar();
            string rootRspPath = Path.Combine(_projectDirectory, "csc.rsp");
            File.WriteAllLines(rootRspPath, new[] { SidecarArgument });
            Directory.CreateDirectory(_testRspFilePath);
            List<string> imports = new();
            bool importPending = false;

            Assert.Throws<IOException>(
                () =>
                    SetupCscRsp.SynchronizeResponseFiles(
                        _assetsDirectory,
                        imports.Add,
                        ref importPending
                    ),
                "A directory at the destination must report a failed response-file write."
            );

            AssertResponseFile(
                rootRspPath,
                new[] { SidecarArgument },
                "Failed destination write must preserve the legacy file"
            );
            Assert.IsEmpty(imports, "A failed write must not import a response file.");
            Assert.IsFalse(
                importPending,
                "No import is pending until the destination has been written."
            );
            Assert.IsEmpty(
                Directory.GetFiles(_assetsDirectory, ".csc.rsp.*.tmp"),
                "Failed writes must remove temporary response files."
            );

            Directory.Delete(_testRspFilePath);
            SetupCscRsp.SynchronizeResponseFiles(_assetsDirectory, imports.Add, ref importPending);

            AssertResponseFile(
                _testRspFilePath,
                new[] { SidecarArgument },
                "Retry must create the active response file"
            );
            Assert.IsFalse(
                File.Exists(rootRspPath),
                "Successful retry must retire the package-only root file."
            );
            CollectionAssert.AreEqual(
                new[] { "Assets/csc.rsp" },
                imports,
                "Retry must import the asset once."
            );
        }

        [Test]
        public void FailedImportRemainsPendingAndPreservesLegacyFileUntilRetry()
        {
            CreateSidecar();
            string rootRspPath = Path.Combine(_projectDirectory, "csc.rsp");
            File.WriteAllLines(rootRspPath, new[] { SidecarArgument });
            bool importPending = false;
            Assert.Throws<IOException>(
                () =>
                    SetupCscRsp.SynchronizeResponseFiles(
                        _assetsDirectory,
                        _ => throw new IOException("Injected import failure"),
                        ref importPending
                    ),
                "An import failure must remain observable to the setup error handler."
            );

            Assert.IsTrue(
                importPending,
                "A written response file still needs its failed import retried."
            );
            AssertResponseFile(
                _testRspFilePath,
                new[] { SidecarArgument },
                "Destination must exist before import"
            );
            AssertResponseFile(
                rootRspPath,
                new[] { SidecarArgument },
                "Failed import must preserve legacy wiring"
            );

            List<string> imports = new();
            SetupCscRsp.SynchronizeResponseFiles(_assetsDirectory, imports.Add, ref importPending);
            Assert.IsFalse(importPending, "Successful retry must clear the pending import.");
            Assert.IsFalse(
                File.Exists(rootRspPath),
                "Successful import must allow legacy cleanup."
            );
            SetupCscRsp.SynchronizeResponseFiles(_assetsDirectory, imports.Add, ref importPending);
            CollectionAssert.AreEqual(
                new[] { "Assets/csc.rsp" },
                imports,
                "An unchanged destination must retry the failed import once."
            );
        }

        private void CreateSidecar()
        {
            string sidecarPath = Path.Combine(
                _projectDirectory,
                DxMessagingBaseCallIgnoreSync.SidecarAssetPath
            );
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath));
            File.WriteAllText(sidecarPath, "Consumer.IgnoredType");
        }

        private static void AssertResponseFile(string path, string[] expectedLines, string context)
        {
            Assert.AreEqual(
                expectedLines != null,
                File.Exists(path),
                $"Unexpected response-file presence at {path}: {context}."
            );
            if (expectedLines != null)
            {
                CollectionAssert.AreEqual(
                    expectedLines,
                    File.ReadAllLines(path),
                    $"Unexpected response-file contents at {path}: {context}."
                );
            }
        }

        [TestCase("-define:FOO # ")]
        [TestCase("-define:FOO #comment ")]
        public void InlineCommentsDoNotCountAsActiveSidecarWiring(string prefix)
        {
            string input = prefix + SidecarArgument;
            string[] actual = SetupCscRsp.SynchronizeAdditionalFileForIgnoreListLines(
                new[] { input },
                DxMessagingBaseCallIgnoreSync.SidecarAssetPath,
                true,
                out bool modified
            );
            CollectionAssert.AreEqual(
                new[] { input, SidecarArgument },
                actual,
                $"Commented sidecar options must stay inactive for prefix [{prefix}]."
            );
            Assert.IsTrue(modified, $"Missing active wiring must be added for prefix [{prefix}].");
        }

        [TestCase(",")]
        [TestCase(";")]
        public void MixedAnalyzerListsPreserveConsumerAnalyzers(string separator)
        {
            string input =
                "  /analyzer:WallstopStudios.DxMessaging.Analyzer.dll"
                + separator
                + "\"Other,Analyzer.dll\""
                + separator
                + "Third.dll  # consumer";
            string[] actual = SetupCscRsp.CleanDxMessagingAnalyzerLines(
                new[] { input },
                out bool modified
            );
            CollectionAssert.AreEqual(
                new[]
                {
                    "  /analyzer:\"Other,Analyzer.dll\"" + separator + "Third.dll  # consumer",
                },
                actual,
                $"Only managed paths may be removed from analyzer lists separated by [{separator}]."
            );
            Assert.IsTrue(
                modified,
                $"The managed analyzer must be removed for separator [{separator}]."
            );
        }

        [TestCase("utf8", "\r\n")]
        [TestCase("utf8-bom", "\n")]
        [TestCase("utf16-le", "\r\n")]
        [TestCase("utf16-be", "\n")]
        [TestCase("utf32-le", "\r")]
        [TestCase("utf32-be", "\r\n")]
        public void SynchronizationPreservesConsumerEncodingAndWhitespace(
            string encodingName,
            string newline
        )
        {
            CreateSidecar();
            Encoding encoding = encodingName switch
            {
                "utf8" => new UTF8Encoding(false),
                "utf8-bom" => new UTF8Encoding(true),
                "utf16-le" => new UnicodeEncoding(false, true),
                "utf16-be" => new UnicodeEncoding(true, true),
                "utf32-le" => new UTF32Encoding(false, true),
                "utf32-be" => new UTF32Encoding(true, true),
                _ => throw new ArgumentException(encodingName),
            };
            string original = "  # Consumer comment" + newline + " \t\n" + "  -define:FOO";
            File.WriteAllText(_testRspFilePath, original, encoding);
            string rootRspPath = Path.Combine(_projectDirectory, "csc.rsp");
            File.WriteAllText(
                rootRspPath,
                original + newline + SidecarArgument + newline,
                encoding
            );
            bool importPending = false;
            List<string> imports = new();
            SetupCscRsp.SynchronizeResponseFiles(_assetsDirectory, imports.Add, ref importPending);

            string expected = original + newline + SidecarArgument + newline;
            byte[] expectedBytes = CombineEncodingPreamble(encoding, expected);
            string context = $"encoding={encodingName}, newline=[{newline}]";
            CollectionAssert.AreEqual(
                expectedBytes,
                File.ReadAllBytes(_testRspFilePath),
                $"Asset content, mixed newlines, whitespace and BOM must survive synchronization: {context}."
            );
            CollectionAssert.AreEqual(
                CombineEncodingPreamble(encoding, original + newline),
                File.ReadAllBytes(rootRspPath),
                $"Root content, mixed newlines, whitespace and BOM must survive migration: {context}."
            );
            SetupCscRsp.SynchronizeResponseFiles(_assetsDirectory, imports.Add, ref importPending);
            CollectionAssert.AreEqual(
                expectedBytes,
                File.ReadAllBytes(_testRspFilePath),
                $"A second sync must preserve identical bytes: {context}."
            );
            CollectionAssert.AreEqual(
                new[] { "Assets/csc.rsp" },
                imports,
                $"A second sync must not import again: {context}."
            );
        }

        [TestCase(",", true)]
        [TestCase(";", true)]
        [TestCase(",", false)]
        [TestCase(";", false)]
        public void MixedAdditionalFileListsPreserveConsumerFiles(
            string separator,
            bool sidecarExists
        )
        {
            string input =
                "  /additionalfile:\"Other,File.txt\""
                + separator
                + "Assets/Editor/DxMessaging.BaseCallIgnore.txt"
                + separator
                + "Third.txt  # consumer";
            string retained = "  /additionalfile:\"Other,File.txt\"" + separator + "Third.txt";
            string expected =
                retained + (sidecarExists ? " " + SidecarArgument : "") + "  # consumer";
            string[] actual = SetupCscRsp.SynchronizeAdditionalFileForIgnoreListLines(
                new[] { input },
                DxMessagingBaseCallIgnoreSync.SidecarAssetPath,
                sidecarExists,
                out bool modified
            );
            string context = $"separator=[{separator}], sidecarExists={sidecarExists}";
            CollectionAssert.AreEqual(
                new[] { expected },
                actual,
                $"Consumer additional files must remain registered: {context}."
            );
            Assert.IsTrue(modified, $"The managed option must be synchronized: {context}.");
            CollectionAssert.AreEqual(
                actual,
                SetupCscRsp.SynchronizeAdditionalFileForIgnoreListLines(
                    actual,
                    DxMessagingBaseCallIgnoreSync.SidecarAssetPath,
                    sidecarExists,
                    out bool modifiedAgain
                ),
                $"List synchronization must be idempotent: {context}."
            );
            Assert.IsFalse(
                modifiedAgain,
                $"A second pass must not change list contents: {context}."
            );
        }

        [TestCase("-r:Folder#Name.dll ")]
        [TestCase("-r:\"Folder # Name.dll\" ")]
        [TestCase("-r:Folder\\\"Name.dll ")]
        public void HashesAndEscapedQuotesInsideArgumentsDoNotHideActiveOptions(string prefix)
        {
            string input = prefix + SidecarArgument;
            string[] actual = SetupCscRsp.SynchronizeAdditionalFileForIgnoreListLines(
                new[] { input },
                DxMessagingBaseCallIgnoreSync.SidecarAssetPath,
                true,
                out bool modified
            );
            CollectionAssert.AreEqual(
                new[] { input },
                actual,
                $"An embedded hash or escaped quote must not hide active options after [{prefix}]."
            );
            Assert.IsFalse(
                modified,
                $"Existing active options after [{prefix}] must remain unchanged."
            );
        }

        [TestCase("-a:OtherWallstopStudios.DxMessaging.Analyzer.dll")]
        [TestCase(
            "-a:Packages/com.wallstop-studios.dxmessaging.extension/Microsoft.CodeAnalysis.dll"
        )]
        [TestCase(
            "-a:Assets/Plugins/Editor/WallstopStudios.DxMessaging.Custom/Microsoft.CodeAnalysis.dll"
        )]
        [TestCase("-additionalfile:Assets/Consumer.DxMessaging.BaseCallIgnore.Notes.txt")]
        [TestCase("-additionalfile:Assets/DxMessaging.BaseCallIgnore/Consumer.txt")]
        public void ConsumerOptionsWithSimilarNamesArePreserved(string argument)
        {
            string[] cleaned = SetupCscRsp.CleanDxMessagingAnalyzerLines(
                new[] { argument },
                out bool removedAnalyzer
            );
            string[] synchronized = SetupCscRsp.SynchronizeAdditionalFileForIgnoreListLines(
                cleaned,
                DxMessagingBaseCallIgnoreSync.SidecarAssetPath,
                false,
                out bool removedSidecar
            );
            CollectionAssert.AreEqual(
                new[] { argument },
                synchronized,
                $"A similar name does not make a consumer option package-owned: [{argument}]."
            );
            Assert.IsFalse(
                removedAnalyzer || removedSidecar,
                $"No package-managed path was present in [{argument}]."
            );
        }

        private static byte[] CombineEncodingPreamble(Encoding encoding, string text)
        {
            byte[] preamble = encoding.GetPreamble();
            byte[] encoded = encoding.GetBytes(text);
            byte[] bytes = new byte[preamble.Length + encoded.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(encoded, 0, bytes, preamble.Length, encoded.Length);
            return bytes;
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
                    "Microsoft.CodeAnalysis.CSharp.dll",
                    "Microsoft.CodeAnalysis.CSharp.dll.meta",
                    "System.Buffers.dll",
                    "System.Buffers.dll.meta",
                    "System.Collections.Immutable.dll",
                    "System.Collections.Immutable.dll.meta",
                    "System.Memory.dll",
                    "System.Memory.dll.meta",
                    "System.Numerics.Vectors.dll",
                    "System.Numerics.Vectors.dll.meta",
                    "System.Reflection.Metadata.dll",
                    "System.Reflection.Metadata.dll.meta",
                    "System.Runtime.CompilerServices.Unsafe.dll",
                    "System.Runtime.CompilerServices.Unsafe.dll.meta",
                    "System.Text.Encoding.CodePages.dll",
                    "System.Text.Encoding.CodePages.dll.meta",
                    "System.Text.Encodings.Web.dll",
                    "System.Text.Encodings.Web.dll.meta",
                    "System.Threading.Tasks.Extensions.dll",
                    "System.Threading.Tasks.Extensions.dll.meta",
                },
                true
            ).SetName("removes a folder containing the full known historical analyzer payload");

            yield return new TestCaseData(
                new[] { "WallstopStudios.DxMessaging.SourceGenerators.dll" },
                true
            ).SetName("removes a 2.x folder containing only the first-party source generator DLL");

            yield return new TestCaseData(
                new[]
                {
                    "WallstopStudios.DxMessaging.SourceGenerators.dll",
                    "WallstopStudios.DxMessaging.SourceGenerators.dll.meta",
                    "Microsoft.CodeAnalysis.dll",
                    "Microsoft.CodeAnalysis.CSharp.dll",
                    "System.Buffers.dll",
                    "System.Memory.dll",
                    "System.Numerics.Vectors.dll",
                    "System.Text.Encoding.CodePages.dll",
                    "System.Text.Encodings.Web.dll",
                    "System.Threading.Tasks.Extensions.dll",
                },
                true
            ).SetName("removes a 2.x folder containing historical Roslyn dependency DLLs");

            yield return new TestCaseData(
                new[]
                {
                    "WallstopStudios.DxMessaging.SourceGenerators.dll",
                    "WallstopStudios.DxMessaging.Analyzer.dll",
                },
                true
            ).SetName("removes a 3.x folder containing both first-party compiler DLLs");

            yield return new TestCaseData(
                new[]
                {
                    "Assets/Plugins/Editor/WallstopStudios.DxMessaging/WallstopStudios.DxMessaging.SourceGenerators.dll",
                    @"Assets\Plugins\Editor\WallstopStudios.DxMessaging\WallstopStudios.DxMessaging.Analyzer.DLL",
                    "Assets/Plugins/Editor/WallstopStudios.DxMessaging/Microsoft.CodeAnalysis.DLL.META",
                },
                true
            ).SetName("matches known DLL names case-insensitively from platform paths");

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
                new[]
                {
                    "WallstopStudios.DxMessaging.SourceGenerators.dll",
                    "WallstopStudios.DxMessaging.SourceGenerators.dll.meta",
                    "WallstopStudios.DxMessaging.Analyzer.dll",
                    "WallstopStudios.DxMessaging.Analyzer.dll.meta",
                    "SomeOtherPlugin.dll",
                },
                false
            ).SetName("preserves the folder when a consumer added a foreign DLL");

            yield return new TestCaseData(
                new[]
                {
                    "WallstopStudios.DxMessaging.SourceGenerators.dll",
                    "WallstopStudios.DxMessaging.Analyzer.dll",
                    "SomeOtherPlugin.dll.meta",
                },
                false
            ).SetName("preserves the folder when a consumer added a foreign DLL meta sidecar");

            yield return new TestCaseData(
                new[] { "WallstopStudios.DxMessaging.Analyzer.dll" },
                false
            ).SetName("preserves the folder when the source generator marker DLL is missing");

            yield return new TestCaseData(
                new[] { "WallstopStudios.DxMessaging.Analyzer.dll", "NestedFolder" },
                false
            ).SetName("preserves the folder when an entry is neither a .dll nor a .dll.meta");

            yield return new TestCaseData(
                new[]
                {
                    "WallstopStudios.DxMessaging.SourceGenerators.dll",
                    "WallstopStudios.DxMessaging.Analyzer.dll",
                    "wallstopstudios.dxmessaging.analyzer.dll",
                },
                false
            ).SetName("preserves the folder when duplicate DLL names differ only by case");

            yield return new TestCaseData(Array.Empty<string>(), false).SetName(
                "does nothing for an empty folder"
            );

            yield return new TestCaseData(
                new[]
                {
                    null,
                    string.Empty,
                    "WallstopStudios.DxMessaging.SourceGenerators.dll",
                    "WallstopStudios.DxMessaging.Analyzer.dll",
                },
                true
            ).SetName("ignores null and empty entries from defensive callers");

            yield return new TestCaseData(
                new[] { "WallstopStudios.DxMessaging.Analyzer.dll.meta" },
                false
            ).SetName("does nothing when only orphaned known meta sidecars remain");
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
