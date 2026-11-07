#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER && DXM_505_COLLECTIONS_PRESENT && DXM_505_BURST_PRESENT
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using global::Unity.Collections.LowLevel.Unsafe;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class GpuPureTests : UnityFixtureBase
    {
        private readonly List<GpuPureOwner> _owners = new();
        private static GpuPureTests _retainedFixture;
        private const string AssetPath =
            "Packages/com.wallstop-studios.dxmessaging/Tests/Runtime/NativeCollectionsResearch/GpuPureKernels.compute";
        private ComputeShader _asset;
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

        public sealed class Case
        {
            internal readonly int Count,
                Batch,
                Rounds,
                Vector;

            internal Case(int count, int batch, int rounds, int vector = -1)
            {
                Count = count;
                Batch = batch;
                Rounds = rounds;
                Vector = vector;
            }

            public override string ToString() =>
                string.Format(
                    CultureInfo.InvariantCulture,
                    "count{0}-batch{1}-rounds{2}-vector{3}",
                    Count,
                    Batch,
                    Rounds,
                    Vector
                );
        }

        public static IEnumerable<Case> Matrix()
        {
            foreach (int count in new[] { 0, 1, 3, 4, 5, 257, 1027 })
            foreach (int batch in new[] { 1, 4, 16, 64, 128, 256 })
            foreach (int rounds in new[] { 0, 3 })
                yield return new Case(count, batch, rounds);
        }

        public static IEnumerable<Case> Goldens()
        {
            for (int vector = 0; vector < 6; ++vector)
                foreach (int rounds in new[] { 0, 3, 64 })
                    yield return new Case(1, 256, rounds, vector);
        }

        public static IEnumerable<Case> ThreadGroupEdges()
        {
            foreach (int count in new[] { 63, 64, 65 })
                yield return new Case(count, 256, 3);
        }

        public static int[] IsolationBatches => new[] { 1, 64 };
        public static int[] PendingOperations => new[] { 0, 1, 2 };

        [OneTimeSetUp]
        public void RequireActualMetalComputeAndObservationCapabilities()
        {
            Assert.That(_retainedFixture, Is.Null, "Prior GPU fixture still owns resources.");
            Assert.That(SystemInfo.supportsComputeShaders, Is.True);
            Assert.That(SystemInfo.supportsGraphicsFence, Is.True);
            Assert.That(SystemInfo.supportsAsyncGPUReadback, Is.True);
            _asset = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetPath);
            Assert.That(_asset, Is.Not.Null);
            _retainedFixture = this;
            TestContext.WriteLine(
                $"GPU pure capabilities: device={SystemInfo.graphicsDeviceName};api={SystemInfo.graphicsDeviceType};compute=true;fence=true;asyncReadback=true"
            );
        }

        [OneTimeTearDown]
        public void RequireAllOwnedRootsRetired()
        {
            Assert.That(_owners, Is.Empty, "Retain this fixture until pending owners are drained.");
            Assert.That(_retainedFixture, Is.SameAs(this));
            _asset = null;
            _retainedFixture = null;
        }

        public override void TearDownManagedResources()
        {
            /*
                Defer tracked cleanup until UnityTearDown has observed completion of every
                submitted GPU command and readback, including when an assertion interrupted a test.
            */
        }

        [UnityTearDown]
        public IEnumerator DrainKnownWorkAndReleaseOwnedRoots()
        {
            int ownerCount = _owners.Count;
            List<Exception> failures = new();
            foreach (GpuPureOwner owner in _owners)
                while (owner.HasPending)
                {
                    try
                    {
                        owner.PollCompletion();
                    }
                    catch (Exception failure)
                    {
                        failures.Add(failure);
                        break;
                    }
                    if (owner.HasPending)
                        yield return null;
                }
            foreach (GpuPureOwner owner in _owners)
            {
                try
                {
                    bool readbackError = owner.HasReadbackError;
                    owner.Dispose();
                    owner.Dispose();
                    Assert.That(owner.HasPending || owner.HasResources, Is.False);
                    Assert.That(owner.ReleaseCount, Is.EqualTo(4));
                    Assert.That(owner.ScratchCleared, Is.True);
                    Assert.That(readbackError, Is.False);
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
            _owners.RemoveAll(owner =>
                !owner.HasPending && !owner.HasResources && owner.ScratchCleared
            );
            if (_owners.Count == 0)
            {
                base.TearDownManagedResources();
                TestContext.WriteLine(
                    $"GPU pure cleanup: owners={ownerCount};four resource roots each released once after actual completion;tracked shader clones retired through base cleanup."
                );
            }
            else
                failures.Add(
                    new InvalidOperationException(
                        "Retain fixture and clones until every GPU root is released."
                    )
                );
            yield return null;
            if (failures.Count != 0)
                throw new AggregateException(failures);
        }

        private GpuPureOwner Create(int count)
        {
            ComputeShader clone = Track(UnityEngine.Object.Instantiate(_asset));
            clone.hideFlags = HideFlags.HideAndDontSave;
            GpuPureOwner owner = TrackDisposable(new GpuPureOwner(count, clone));
            _owners.Add(owner);
            return owner;
        }

        private static IEnumerator Complete(GpuPureOwner owner)
        {
            while (!owner.PollCompletion())
                yield return null;
            Assert.That(owner.HasReadbackError, Is.False);
            Assert.That(owner.HasPending, Is.False);
        }

        private static long Consume(long value, int index)
        {
            unchecked
            {
                ulong bits = (ulong)value;
                return (long)((bits ^ (bits << 7)) + (ulong)(index + 1) * 97);
            }
        }

        private static void Verify(GpuPureOwner owner, int batch, int rounds, string kind)
        {
            Assert.That(owner.ObservedInput, Is.Not.Null);
            Assert.That(owner.ObservedStage, Is.Not.Null);
            Assert.That(owner.ObservedFinal, Is.Not.Null);
            CollectionAssert.AreEqual(owner.OriginalInput, owner.ObservedInput);
            long[] stage = owner.ObservedStage.Select(v => v.Signed).ToArray();
            long[] final = owner.ObservedFinal.Select(v => v.Signed).ToArray();
            Assert.That(stage.Length, Is.EqualTo(owner.Count + 2));
            Assert.That(final.Length, Is.EqualTo(owner.Count + 2));
            Assert.That(stage[0], Is.EqualTo(GpuPureOwner.Guard));
            Assert.That(stage[owner.Count + 1], Is.EqualTo(GpuPureOwner.Guard));
            Assert.That(final[0], Is.EqualTo(GpuPureOwner.Guard));
            Assert.That(final[owner.Count + 1], Is.EqualTo(GpuPureOwner.Guard));
            long stageSum = 0,
                finalSum = 0;
            for (int i = 0; i < owner.Count; ++i)
            {
                long expected = PureBatchKernels.Compute(owner.OriginalInput[i + 1], rounds);
                Assert.That(
                    stage[i + 1],
                    Is.EqualTo(expected),
                    $"{kind}: count={owner.Count};batch={batch};rounds={rounds};index={i}"
                );
                Assert.That(
                    final[i + 1],
                    Is.EqualTo(Consume(expected, i)),
                    $"{kind}: count={owner.Count};batch={batch};rounds={rounds};index={i}"
                );
                stageSum = unchecked(stageSum + stage[i + 1]);
                finalSum = unchecked(finalSum + final[i + 1]);
            }
            int chunks = (owner.Count + batch - 1) / batch,
                groups = 0;
            for (int offset = 0; offset < owner.Count; offset += batch)
                groups += ((Math.Min(batch, owner.Count - offset) + 63) / 64) * 2;
            Assert.That(owner.Dispatches, Is.EqualTo(chunks * 2));
            Assert.That(owner.Groups, Is.EqualTo(groups));
            TestContext.WriteLine(
                $"GPU pure output: kind={kind};count={owner.Count};batch={batch};rounds={rounds};dispatches={owner.Dispatches};groups={owner.Groups};stageSum={stageSum};finalSum={finalSum};stage={string.Join(",", stage.Skip(1).Take(owner.Count))};final={string.Join(",", final.Skip(1).Take(owner.Count))};timing=unmeasured"
            );
        }

        [UnityTest]
        public IEnumerator GpuConsumerUsesResidentStageBeforeUntimedOracle(
            [ValueSource(nameof(Matrix))] Case row
        )
        {
            GpuPureOwner owner = Create(row.Count);
            Assert.That(owner.TrySubmit(row.Batch, row.Rounds), Is.True, row.ToString());
            owner.BeginUntimedObservation();
            yield return Complete(owner);
            Verify(owner, row.Batch, row.Rounds, "matrix");
        }

        [UnityTest]
        public IEnumerator SignedGoldenVectorsIncludeMaximumRoundCount(
            [ValueSource(nameof(Goldens))] Case row
        )
        {
            long[] maximum =
            {
                -4786109065817771447L,
                2087148930895262372L,
                -449705407519791139L,
                -7771358583956589301L,
                -4743555500161493718L,
                -8903653924201765931L,
            };
            GpuPureOwner owner = Create(1);
            owner.OriginalInput[1] = new NativeNumericPayload
            {
                Producer = Producers[row.Vector],
                Sequence = Sequences[row.Vector],
                Value = Values[row.Vector],
            };
            owner.Reset();
            Assert.That(owner.TrySubmit(row.Batch, row.Rounds), Is.True, row.ToString());
            owner.BeginUntimedObservation();
            yield return Complete(owner);
            long expected =
                row.Rounds == 0 ? Simple[row.Vector]
                : row.Rounds == 3 ? Mixed[row.Vector]
                : maximum[row.Vector];
            Assert.That(owner.ObservedStage[1].Signed, Is.EqualTo(expected), row.ToString());
            Verify(
                owner,
                row.Batch,
                row.Rounds,
                "golden" + row.Vector.ToString(CultureInfo.InvariantCulture)
            );
        }

        [UnityTest]
        public IEnumerator ThreadGroupTailNeverWritesOuterGuards(
            [ValueSource(nameof(ThreadGroupEdges))] Case row
        )
        {
            GpuPureOwner owner = Create(row.Count);
            Assert.That(owner.TrySubmit(row.Batch, row.Rounds), Is.True, row.ToString());
            owner.BeginUntimedObservation();
            yield return Complete(owner);
            Verify(owner, row.Batch, row.Rounds, "edge");
        }

        [UnityTest]
        public IEnumerator PendingOperationsRejectBeforeResourceReuse(
            [ValueSource(nameof(PendingOperations))] int operation
        )
        {
            GpuPureOwner owner = Create(257);
            Assert.That(owner.TrySubmit(64, 3), Is.True);
            if (operation == 0)
                Assert.Throws<InvalidOperationException>(() => owner.Reset());
            else if (operation == 1)
                Assert.That(owner.TrySubmit(1, 0), Is.False);
            else
                Assert.Throws<InvalidOperationException>(() => owner.Dispose());
            Assert.That(owner.HasBuffers, Is.True);
            Assert.That(owner.ReleaseCount, Is.EqualTo(0));
            owner.BeginUntimedObservation();
            yield return Complete(owner);
            Verify(owner, 64, 3, "pending" + operation.ToString(CultureInfo.InvariantCulture));
        }

        [UnityTest]
        public IEnumerator SeparateShaderBindingsAndBuffersSurviveOtherOwnerRelease(
            [ValueSource(nameof(IsolationBatches))] int batch
        )
        {
            GpuPureOwner first = Create(17),
                second = Create(17);
            second.OriginalInput[1] = new NativeNumericPayload
            {
                Producer = -11,
                Sequence = 19,
                Value = long.MinValue,
            };
            second.Reset();
            Assert.That(first.TrySubmit(batch, 0), Is.True);
            Assert.That(second.TrySubmit(batch, 3), Is.True);
            first.BeginUntimedObservation();
            second.BeginUntimedObservation();
            while (first.HasPending || second.HasPending)
            {
                if (first.HasPending)
                    first.PollCompletion();
                if (second.HasPending)
                    second.PollCompletion();
                if (first.HasPending || second.HasPending)
                    yield return null;
            }
            Verify(first, batch, 0, "isolation-first");
            Verify(second, batch, 3, "isolation-second");
            first.Dispose();
            second.Reset();
            Assert.That(second.TrySubmit(batch, 0), Is.True);
            second.BeginUntimedObservation();
            yield return Complete(second);
            Verify(second, batch, 0, "isolation-survivor");
        }

        [UnityTest]
        public IEnumerator ResetRequiresCompletionAndAllowsIndependentCycles()
        {
            GpuPureOwner owner = Create(17);
            for (int rounds = 0; rounds < 4; ++rounds)
            {
                owner.Reset();
                Assert.That(
                    owner.ObservedInput == null
                        && owner.ObservedStage == null
                        && owner.ObservedFinal == null,
                    Is.True
                );
                Assert.That(owner.TrySubmit(4, rounds), Is.True);
                owner.BeginUntimedObservation();
                Assert.Throws<InvalidOperationException>(() => owner.BeginUntimedObservation());
                yield return Complete(owner);
                Verify(owner, 4, rounds, "reuse");
                Assert.Throws<InvalidOperationException>(() => owner.TrySubmit(4, rounds));
            }
            Assert.That(owner.CompletedSubmissions, Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator DisposalAfterKnownCompletionRejectsStaleCalls()
        {
            GpuPureOwner owner = Create(4);
            Assert.That(owner.TrySubmit(4, 3), Is.True);
            owner.BeginUntimedObservation();
            yield return Complete(owner);
            Verify(owner, 4, 3, "dispose");
            owner.Dispose();
            owner.Dispose();
            Assert.That(owner.ScratchCleared, Is.True);
            Assert.Throws<ObjectDisposedException>(() => owner.TrySubmit(1, 0));
            Assert.Throws<ObjectDisposedException>(() => owner.Reset());
            Assert.Throws<ObjectDisposedException>(() => owner.PollCompletion());
            Assert.Throws<ObjectDisposedException>(() => owner.BeginUntimedObservation());
        }

        [Test]
        public void InvalidArgumentsRejectBeforeGpuSubmission()
        {
            GpuPureOwner owner = Create(4);
            foreach (int batch in new[] { -1, 0, 257 })
                Assert.Throws<ArgumentOutOfRangeException>(() => owner.TrySubmit(batch, 0));
            foreach (int rounds in new[] { -1, 65 })
                Assert.Throws<ArgumentOutOfRangeException>(() => owner.TrySubmit(1, rounds));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuPureOwner(-1, _asset));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuPureOwner(4097, _asset));
            Assert.Throws<ArgumentNullException>(() => new GpuPureOwner(1, null));
            Assert.Throws<InvalidOperationException>(() => owner.BeginUntimedObservation());
            Assert.That(owner.HasPending, Is.False);
            Assert.That(owner.Dispatches, Is.EqualTo(0));
        }

        private struct ManagedReferencePayload
        {
            public object Reference;
        }

        [Test]
        public void BufferAbiUsesClosedBlittableWordsWithoutManagedReferences()
        {
            Assert.That(Marshal.SizeOf<NativeNumericPayload>(), Is.EqualTo(16));
            Assert.That(Marshal.SizeOf<GpuWordPair>(), Is.EqualTo(8));
            Assert.That(
                Marshal.OffsetOf<GpuWordPair>(nameof(GpuWordPair.Low)).ToInt32(),
                Is.EqualTo(0)
            );
            Assert.That(
                Marshal.OffsetOf<GpuWordPair>(nameof(GpuWordPair.High)).ToInt32(),
                Is.EqualTo(4)
            );
            Assert.That(UnsafeUtility.IsBlittable<NativeNumericPayload>(), Is.True);
            Assert.That(UnsafeUtility.IsBlittable<ManagedReferencePayload>(), Is.False);
            ManagedReferencePayload managed = new() { Reference = new object() };
            Assert.That(managed.Reference, Is.Not.Null);
        }

        [Test]
        public void ForeignThreadOperationsRejectBeforeGpuAccess([Values(0, 1, 2, 3)] int operation)
        {
            GpuPureOwner owner = Create(4);
            Exception caught = null;
            Thread thread = new(() =>
            {
                try
                {
                    switch (operation)
                    {
                        case 0:
                            owner.TrySubmit(1, 0);
                            break;
                        case 1:
                            owner.Reset();
                            break;
                        case 2:
                            owner.PollCompletion();
                            break;
                        case 3:
                            owner.Dispose();
                            break;
                    }
                }
                catch (Exception error)
                {
                    caught = error;
                }
            });
            thread.Start();
            Assert.That(thread.Join(5000), Is.True);
            Assert.That(
                caught,
                Is.TypeOf<InvalidOperationException>(),
                operation.ToString(CultureInfo.InvariantCulture)
            );
            Assert.That(owner.HasPending, Is.False);
            Assert.That(owner.HasBuffers, Is.True);
            Assert.That(owner.ReleaseCount, Is.EqualTo(0));
        }

        [Test]
        public void ActualShaderHasBothExpectedThreadGroups()
        {
            foreach (string name in new[] { "Process", "Consume" })
            {
                int kernel = _asset.FindKernel(name);
                _asset.GetKernelThreadGroupSizes(kernel, out uint x, out uint y, out uint z);
                Assert.That(x, Is.EqualTo(64), name);
                Assert.That(y, Is.EqualTo(1), name);
                Assert.That(z, Is.EqualTo(1), name);
            }
        }
    }
}
#endif
