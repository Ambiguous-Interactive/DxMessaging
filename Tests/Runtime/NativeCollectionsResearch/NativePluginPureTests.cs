// cspell:ignore libdxm
#if UNITY_EDITOR_OSX && UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Threading;
    using global::Unity.Collections.LowLevel.Unsafe;
    using NUnit.Framework;
    using UnityEngine;

    public sealed unsafe class NativePluginPureTests
    {
        private readonly List<NativePluginPureOwner> _owned = new();
        private readonly List<NativePluginLibrary> _libraries = new();
        private static readonly int[] Producers = { 0, 1, -1, int.MinValue, int.MaxValue, 17 };
        private static readonly int[] Sequences = { 0, 1, -1, int.MaxValue, int.MinValue, 29 };
        private static readonly long[] Values =
        {
            0,
            1,
            -1,
            long.MinValue,
            long.MaxValue,
            81985529216486895L,
        };
        private static readonly long[] Simple =
        {
            0,
            99,
            -99,
            9223371830696345599L,
            -9223371830696345698L,
            81985529216488573L,
        };
        private static readonly long[] Mixed =
        {
            6392512781046991133L,
            6133562355747626131L,
            297763689130802543L,
            -8091143960558176489L,
            -362853752901266012L,
            9008981355231998747L,
        };

        private static string Binary(string name = "libdxm_native_plugin.dylib") =>
            Path.Combine(
                Path.GetDirectoryName(Application.dataPath),
                "Packages/com.wallstop-studios.dxmessaging/.artifacts/perf-lab/505-native-plugin-s388/native",
                name
            );

        [OneTimeSetUp]
        public void RequireActualBinaryIdentity()
        {
            string path = Binary();
            Assert.That(File.Exists(path), Is.True, path);
            using SHA256 hash = SHA256.Create();
            using FileStream stream = File.OpenRead(path);
            string actual = BitConverter
                .ToString(hash.ComputeHash(stream))
                .Replace("-", "")
                .ToLowerInvariant();
            string expected = File.ReadAllText(Binary("library.sha256")).Trim();
            Assert.That(actual, Is.EqualTo(expected));
            TestContext.WriteLine(
                $"Native plugin identity: path={path};sha256={actual}; ARM64 compiler evidence retained separately."
            );
        }

        [TearDown]
        public void ReleaseEveryOwnedRootAndLibraryReference()
        {
            List<Exception> failures = new();
            foreach (NativePluginPureOwner owner in _owned)
            {
                try
                {
                    owner.Dispose();
                    owner.Dispose();
                    Assert.That(
                        owner.Input.IsCreated || owner.Output.IsCreated || owner.Modes.IsCreated,
                        Is.False
                    );
                    Assert.That(owner.ReleaseCount, Is.EqualTo(3));
                    AssertClosed(owner.Library);
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
            foreach (NativePluginLibrary library in _libraries)
            {
                try
                {
                    library.Dispose();
                    library.Dispose();
                    AssertClosed(library);
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
            TestContext.WriteLine(
                $"Native plugin cleanup: owners={_owned.Count};extraLibraries={_libraries.Count};three containers per owner and each acquired loader reference released once."
            );
            _owned.Clear();
            _libraries.Clear();
            if (failures.Count != 0)
                throw new AggregateException(failures);
        }

        private static void AssertClosed(NativePluginLibrary library)
        {
            Assert.That(library.RootsCleared, Is.True);
            Assert.That(library.Ready, Is.False);
            Assert.That(library.CloseCount, Is.EqualTo(library.OpenCount));
            if (library.OpenCount != 0)
                Assert.That(library.LastCloseStatus, Is.EqualTo(0));
        }

        private NativePluginPureOwner Create(int count)
        {
            NativePluginPureOwner owner = new(count, Binary());
            _owned.Add(owner);
            return owner;
        }

        private NativePluginLibrary Library()
        {
            NativePluginLibrary library = new();
            _libraries.Add(library);
            return library;
        }

        private static void Guards(NativePluginPureOwner owner)
        {
            Assert.That(owner.Input[0].Value, Is.EqualTo(NativePluginPureOwner.Guard));
            Assert.That(
                owner.Input[owner.Count + 1].Value,
                Is.EqualTo(NativePluginPureOwner.Guard)
            );
            Assert.That(owner.Input[0].Producer, Is.EqualTo(-1));
            Assert.That(owner.Input[owner.Count + 1].Sequence, Is.EqualTo(-2));
            Assert.That(owner.Output[0], Is.EqualTo(NativePluginPureOwner.Guard));
            Assert.That(owner.Output[owner.Count + 1], Is.EqualTo(NativePluginPureOwner.Guard));
            Assert.That(owner.Modes[0], Is.EqualTo(NativePluginPureOwner.ModeGuard));
            Assert.That(owner.Modes[owner.Count + 1], Is.EqualTo(NativePluginPureOwner.ModeGuard));
        }

        private static void Modes(NativePluginPureOwner owner, NativePluginPath path, int batch)
        {
            int width = path == NativePluginPath.NativeScalar ? 1 : batch;
            int entries = (owner.Count + width - 1) / width;
            Assert.That(owner.Entries, Is.EqualTo(entries));
            for (int i = 0; i < owner.Count; ++i)
                Assert.That(
                    owner.Modes[i + 1],
                    Is.EqualTo(
                        i < entries
                            ? path == NativePluginPath.Managed
                                ? 1
                                : 0
                            : -1
                    )
                );
        }

        [Test]
        public void ActualNativeAndManagedPathsPreserveEveryValueAndPartialChunk(
            [Values] NativePluginPath path,
            [Values(0, 1, 3, 4, 5, 257, 1027)] int count,
            [Values(1, 4, 16, 64, 128, 256)] int batch,
            [Values(0, 3)] int rounds
        )
        {
            NativePluginPureOwner owner = Create(count);
            NativeNumericPayload[] original = owner.Input.ToArray();
            long[] expected = original
                .Skip(1)
                .Take(count)
                .Select(payload => PureBatchKernels.Compute(payload, rounds))
                .ToArray();
            owner.Run(path, batch, rounds);
            long[] actual = owner.Output.ToArray().Skip(1).Take(count).ToArray();
            CollectionAssert.AreEqual(expected, actual);
            CollectionAssert.AreEqual(original, owner.Input.ToArray());
            Modes(owner, path, batch);
            Guards(owner);
            long checksum = 0;
            foreach (long value in actual)
                checksum = unchecked(checksum + value);
            TestContext.WriteLine(
                $"Native plugin matrix: path={path};count={count};batch={batch};rounds={rounds};entries={owner.Entries};checksum={checksum};values={string.Join(",", actual)};timing=unmeasured"
            );
        }

        [Test]
        public void IndependentSignedGoldenVectorsMatch(
            [Values] NativePluginPath path,
            [Values(0, 3)] int rounds,
            [Values(0, 1, 2, 3, 4, 5)] int vector
        )
        {
            NativePluginPureOwner owner = Create(1);
            owner.Input[1] = new NativeNumericPayload
            {
                Producer = Producers[vector],
                Sequence = Sequences[vector],
                Value = Values[vector],
            };
            owner.Run(path, 1, rounds);
            Assert.That(owner.Output[1], Is.EqualTo(rounds == 0 ? Simple[vector] : Mixed[vector]));
            Assert.That(
                owner.Output[1],
                Is.EqualTo(PureBatchKernels.Compute(owner.Input[1], rounds))
            );
            Modes(owner, path, 1);
            Guards(owner);
            TestContext.WriteLine(
                $"Native plugin golden: path={path};vector={vector};rounds={rounds};value={owner.Output[1]}"
            );
        }

        [Test]
        public void MaximumRoundsMatchFrozenSignedVectors([Values(0, 1, 2, 3, 4, 5)] int vector)
        {
            long[] expected =
            {
                -4786109065817771447L,
                2087148930895262372L,
                -449705407519791139L,
                -7771358583956589301L,
                -4743555500161493718L,
                -8903653924201765931L,
            };
            NativePluginPureOwner owner = Create(1);
            owner.Input[1] = new NativeNumericPayload
            {
                Producer = Producers[vector],
                Sequence = Sequences[vector],
                Value = Values[vector],
            };
            owner.Run(NativePluginPath.NativeBatch, 256, 64);
            Assert.That(owner.Output[1], Is.EqualTo(expected[vector]));
            Assert.That(owner.Output[1], Is.EqualTo(PureBatchKernels.Compute(owner.Input[1], 64)));
            Modes(owner, NativePluginPath.NativeBatch, 256);
            Guards(owner);
            TestContext.WriteLine(
                $"Native plugin golden: path=NativeBatch;vector={vector};rounds=64;value={owner.Output[1]}"
            );
        }

        [Test]
        public void ActualNativeLayoutMatchesManagedBlittablePayload()
        {
            NativePluginPureOwner owner = Create(0);
            Assert.That(
                UnsafeUtility.SizeOf<NativeNumericPayload>(),
                Is.EqualTo(owner.Library.Layout(0))
            );
            Assert.That(
                Marshal
                    .OffsetOf<NativeNumericPayload>(nameof(NativeNumericPayload.Producer))
                    .ToInt32(),
                Is.EqualTo(owner.Library.Layout(1))
            );
            Assert.That(
                Marshal
                    .OffsetOf<NativeNumericPayload>(nameof(NativeNumericPayload.Sequence))
                    .ToInt32(),
                Is.EqualTo(owner.Library.Layout(2))
            );
            Assert.That(
                Marshal
                    .OffsetOf<NativeNumericPayload>(nameof(NativeNumericPayload.Value))
                    .ToInt32(),
                Is.EqualTo(owner.Library.Layout(3))
            );
            Assert.That(owner.Library.Layout(99), Is.EqualTo(-1));
            TestContext.WriteLine("Native plugin layout: stride=16;producer=0;sequence=4;value=8");
        }

        [Test]
        public void MissingPathExportOrWrongAbiFailsClosed([Values(0, 1, 2)] int failure)
        {
            NativePluginLibrary library = Library();
            string name =
                failure == 0 ? "missing.dylib"
                : failure == 1 ? "libdxm_missing_export.dylib"
                : "libdxm_bad_abi.dylib";
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                library.Load(Binary(name))
            );
            string expected =
                failure == 0 ? "open failed"
                : failure == 1 ? "export is missing"
                : "Unsupported native ABI";
            StringAssert.Contains(expected, exception.Message);
            Assert.That(library.OpenCount, Is.EqualTo(failure == 0 ? 0 : 1));
            AssertClosed(library);
        }

        [Test]
        public void RawNativeValidationRejectsBeforeAnyWrite(
            [Values(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14)] int invalid
        )
        {
            NativePluginPureOwner owner = Create(4);
            NativeNumericPayload[] original = owner.Input.ToArray();
            long[] outputBefore = owner.Output.ToArray();
            int[] modesBefore = owner.Modes.ToArray();
            NativeNumericPayload* input =
                (NativeNumericPayload*)owner.Input.GetUnsafeReadOnlyPtr() + 1;
            long* output = (long*)owner.Output.GetUnsafePtr() + 1;
            int* mode = (int*)owner.Modes.GetUnsafePtr() + 1;
            int count = 4,
                rounds = 3,
                inputCapacity = 4,
                outputCapacity = 4;
            switch (invalid)
            {
                case 0:
                    count = -1;
                    break;
                case 1:
                    count = 4097;
                    break;
                case 2:
                    rounds = -1;
                    break;
                case 3:
                    rounds = 65;
                    break;
                case 4:
                    inputCapacity = 3;
                    break;
                case 5:
                    outputCapacity = 3;
                    break;
                case 6:
                    input = null;
                    break;
                case 7:
                    output = null;
                    break;
                case 8:
                    mode = null;
                    break;
                case 9:
                    input = (NativeNumericPayload*)((byte*)input + 1);
                    break;
                case 10:
                    output = (long*)((byte*)output + 1);
                    break;
                case 11:
                    mode = (int*)((byte*)mode + 1);
                    break;
                case 12:
                    output = (long*)input;
                    break;
                case 13:
                    mode = (int*)input;
                    break;
                case 14:
                    mode = (int*)output;
                    break;
            }
            Assert.That(
                owner.Library.Invoke(
                    count,
                    input,
                    output,
                    mode,
                    rounds,
                    inputCapacity,
                    outputCapacity
                ),
                Is.EqualTo(1)
            );
            CollectionAssert.AreEqual(original, owner.Input.ToArray());
            CollectionAssert.AreEqual(outputBefore, owner.Output.ToArray());
            CollectionAssert.AreEqual(modesBefore, owner.Modes.ToArray());
            Guards(owner);
        }

        [Test]
        public void EmptyRawCallAcceptsNullWithoutAccess()
        {
            NativePluginPureOwner owner = Create(0);
            Assert.That(owner.Library.Invoke(0, null, null, null, 0, 0, 0), Is.EqualTo(0));
            Assert.That(owner.Entries, Is.EqualTo(0));
            Guards(owner);
        }

        [Test]
        public void ResetReusePreservesInputAcrossAllPaths()
        {
            NativePluginPureOwner owner = Create(17);
            NativeNumericPayload[] original = owner.Input.ToArray();
            foreach (NativePluginPath path in Enum.GetValues(typeof(NativePluginPath)))
            {
                for (int rounds = 0; rounds < 4; ++rounds)
                {
                    owner.Reset();
                    owner.Run(path, 4, rounds);
                    Assert.Throws<InvalidOperationException>(() => owner.Run(path, 4, rounds));
                    for (int i = 0; i < owner.Count; ++i)
                        Assert.That(
                            owner.Output[i + 1],
                            Is.EqualTo(PureBatchKernels.Compute(original[i + 1], rounds))
                        );
                    Modes(owner, path, 4);
                    Guards(owner);
                }
            }
            CollectionAssert.AreEqual(original, owner.Input.ToArray());
        }

        [Test]
        public void DisposalRejectsEveryCallBeforeAccessAndClearsDelegateRoots()
        {
            NativePluginPureOwner owner = Create(4);
            owner.Run(NativePluginPath.NativeBatch, 4, 3);
            owner.Dispose();
            owner.Dispose();
            AssertClosed(owner.Library);
            Assert.That(owner.ReleaseCount, Is.EqualTo(3));
            Assert.Throws<ObjectDisposedException>(() =>
                owner.Run(NativePluginPath.NativeBatch, 1, 0)
            );
            Assert.Throws<ObjectDisposedException>(() => owner.Reset());
            Assert.Throws<ObjectDisposedException>(() => owner.Library.Layout(0));
            Assert.Throws<ObjectDisposedException>(() => owner.Library.Load(Binary()));
            Assert.Throws<ObjectDisposedException>(() =>
                owner.Library.Invoke(0, null, null, null, 0, 0, 0)
            );
        }

        [Test]
        public void InvalidWrapperArgumentsAndCapacityRejectBeforeWrites()
        {
            NativePluginPureOwner owner = Create(4);
            long[] output = owner.Output.ToArray();
            int[] modes = owner.Modes.ToArray();
            foreach (int batch in new[] { -1, 0, 257 })
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    owner.Run(NativePluginPath.NativeBatch, batch, 0)
                );
            foreach (int rounds in new[] { -1, 65 })
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    owner.Run(NativePluginPath.NativeBatch, 1, rounds)
                );
            Assert.Throws<ArgumentOutOfRangeException>(() => owner.Run((NativePluginPath)99, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new NativePluginPureOwner(-1, Binary())
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new NativePluginPureOwner(4097, Binary())
            );
            CollectionAssert.AreEqual(output, owner.Output.ToArray());
            CollectionAssert.AreEqual(modes, owner.Modes.ToArray());
            Assert.That(owner.Entries, Is.EqualTo(0));
        }

        [Test]
        public void ForeignThreadOperationsRejectBeforeNativeAccess(
            [Values(0, 1, 2, 3)] int operation
        )
        {
            NativePluginPureOwner owner = Create(4);
            Exception caught = null;
            Thread thread = new(() =>
            {
                try
                {
                    switch (operation)
                    {
                        case 0:
                            owner.Run(NativePluginPath.NativeBatch, 1, 0);
                            break;
                        case 1:
                            owner.Reset();
                            break;
                        case 2:
                            owner.Dispose();
                            break;
                        case 3:
                            owner.Library.Load(Binary());
                            break;
                    }
                }
                catch (Exception failure)
                {
                    caught = failure;
                }
            });
            thread.Start();
            Assert.That(thread.Join(5000), Is.True);
            Assert.That(caught, Is.TypeOf<InvalidOperationException>());
            Assert.That(owner.ReleaseCount, Is.EqualTo(0));
            Assert.That(owner.Library.Ready, Is.True);
            owner.Run(NativePluginPath.NativeBatch, 4, 0);
            Modes(owner, NativePluginPath.NativeBatch, 4);
            Guards(owner);
        }

        [Test]
        public void ReloadRejectsWithoutClosingLiveReference()
        {
            NativePluginPureOwner owner = Create(4);
            Assert.Throws<InvalidOperationException>(() => owner.Library.Load(Binary()));
            Assert.That(owner.Library.OpenCount, Is.EqualTo(1));
            Assert.That(owner.Library.CloseCount, Is.EqualTo(0));
            owner.Run(NativePluginPath.NativeScalar, 4, 3);
            Modes(owner, NativePluginPath.NativeScalar, 4);
            Guards(owner);
        }

        private struct ManagedReferencePayload
        {
            public object Reference;
        }

        [Test]
        public void ClosedNativePayloadBoundaryExcludesManagedReferences()
        {
            Assert.That(UnsafeUtility.IsBlittable<NativeNumericPayload>(), Is.True);
            Assert.That(UnsafeUtility.IsBlittable<ManagedReferencePayload>(), Is.False);
            ManagedReferencePayload managed = new() { Reference = new object() };
            Assert.That(managed.Reference, Is.Not.Null);
            TestContext.WriteLine(
                "Native ABI has only primitive counts and pointers to the closed numeric payload; no generic object payload accepted."
            );
        }

        [Test]
        public void OneOwnerReleaseDoesNotInvalidateAnotherLiveReference(
            [Values(NativePluginPath.NativeBatch, NativePluginPath.NativeScalar)]
                NativePluginPath path
        )
        {
            NativePluginPureOwner first = Create(17);
            NativePluginPureOwner second = Create(17);
            second.Input[1] = new NativeNumericPayload
            {
                Producer = -11,
                Sequence = 19,
                Value = long.MinValue,
            };
            first.Run(path, 4, 3);
            first.Dispose();
            AssertClosed(first.Library);
            Assert.That(second.Library.CloseCount, Is.EqualTo(0));
            second.Run(path, 4, 3);
            Assert.That(second.Output[1], Is.EqualTo(PureBatchKernels.Compute(second.Input[1], 3)));
            Modes(second, path, 4);
            Guards(second);
        }
    }
}
#endif
