namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.MessageBus.Internal;
    using NUnit.Framework;
#if !UNITY_2021_3_OR_NEWER
    using Assert = NUnit.Framework.Legacy.ClassicAssert;
#endif

    /// <summary>
    /// Contract guardrails for the <see cref="RegistrationMethodAxes"/> table.
    /// These tests are the structural backstop for memory reclamation: they
    /// assert that every
    /// <see cref="RegistrationMethod"/> has an explicit <see cref="SlotKey"/>
    /// mapping and that real (non-sentinel) mappings are unique.
    /// </summary>
    /// <remarks>
    /// 2026-11-07: Portable NUnit 5 retains the original contract assertions.
    /// Conditional classic aliases preserve the Unity assertion API where required.
    /// </remarks>
    [TestFixture]
    [Category("Contract")]
    public sealed class RegistrationMethodAxisCoverageTests
    {
        /// <summary>
        /// Asserts <see cref="RegistrationMethodAxes.GetSlotKey"/> returns a
        /// value for every defined enum value: each lookup either yields a
        /// non-<see cref="SlotKey.None"/> real slot key OR is a member of the
        /// documented sentinel set
        /// (<see cref="RegistrationMethod.GlobalAcceptAll"/>,
        /// <see cref="RegistrationMethod.Interceptor"/>).
        /// </summary>
        [Test]
        public void EveryRegistrationMethodHasExplicitMapping()
        {
            HashSet<RegistrationMethod> sentinels = new()
            {
                RegistrationMethod.GlobalAcceptAll,
                RegistrationMethod.Interceptor,
            };

            foreach (RegistrationMethod method in EnumerateRegistrationMethods())
            {
                SlotKey key = default;
                Assert.DoesNotThrow(
                    () => key = RegistrationMethodAxes.GetSlotKey(method),
                    "GetSlotKey threw for {0}",
                    method
                );
                if (key == SlotKey.None)
                {
                    Assert.IsTrue(
                        sentinels.Contains(method),
                        "Method {0} mapped to SlotKey.None but is not in the documented sentinel set.",
                        method
                    );
                }
                else
                {
                    Assert.IsFalse(
                        sentinels.Contains(method),
                        "Method {0} is in the sentinel set but mapped to a real SlotKey {1}.",
                        method,
                        key
                    );
                }
            }
        }

        /// <summary>
        /// Asserts <see cref="RegistrationMethodAxes"/>'s static type
        /// initializer (which builds and validates the lookup table) does not
        /// throw. Exercises the validation gate from a clean state.
        /// </summary>
        [Test]
        public void RegistrationMethodAxesTypeInitDoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                RuntimeHelpers.RunClassConstructor(typeof(RegistrationMethodAxes).TypeHandle)
            );
        }

        /// <summary>
        /// Asserts <see cref="RegistrationMethodAxes.GetSlotKey"/> safely
        /// returns <see cref="SlotKey.None"/> for cast values that fall
        /// outside the lookup table's bounds (both above and below the
        /// defined ordinal range).
        /// </summary>
        [Test]
        public void GetSlotKeyForOutOfRangeMethodReturnsNone()
        {
            Assert.AreEqual(
                SlotKey.None,
                RegistrationMethodAxes.GetSlotKey((RegistrationMethod)int.MaxValue)
            );
            Assert.AreEqual(
                SlotKey.None,
                RegistrationMethodAxes.GetSlotKey((RegistrationMethod)(-1))
            );
        }

        /// <summary>
        /// Pins the human-readable <see cref="SlotKey.ToString"/> format used
        /// by diagnostics and test assertion messages.
        /// </summary>
        [Test]
        public void SlotKeyToStringFormat()
        {
            Assert.AreEqual("None", SlotKey.None.ToString());
            Assert.AreEqual(
                "Targeted/PostProcess/WithoutContext",
                new SlotKey(
                    DispatchKind.Targeted,
                    DispatchPhase.PostProcess,
                    DispatchVariant.WithoutContext
                ).ToString()
            );
        }

        /// <summary>
        /// Asserts the constructor accepts the maximum legal value on every
        /// axis (kind=15, phase=1, variant=3 -- the largest defined variant).
        /// Pins that <c>SlotKey.None == 0xFF</c> is the only byte value
        /// unreachable from any defined <c>(kind, phase, variant)</c> triple.
        /// </summary>
        [Test]
        public void SlotKeyAcceptsMaxLegalAxisValues()
        {
            // (15 << 4) | (1 << 3) | 3 = 240 | 8 | 3 = 251 = 0xFB
            SlotKey edge = new SlotKey((DispatchKind)15, (DispatchPhase)1, (DispatchVariant)3);
            Assert.AreEqual(0xFB, edge.Packed);
            Assert.AreEqual((DispatchKind)15, edge.Kind);
            Assert.AreEqual((DispatchPhase)1, edge.Phase);
            Assert.AreEqual((DispatchVariant)3, edge.Variant);
            Assert.AreNotEqual(SlotKey.None, edge);
        }

        /// <summary>
        /// Pins the deliberate aliasing between <c>default(SlotKey)</c> and
        /// <c>new SlotKey(Untargeted, Handle, Default)</c>. This is intentional
        /// -- uninitialized fields decode to a real, valid slot. The "no slot
        /// applies" sentinel is <see cref="SlotKey.None"/>, never
        /// <c>default(SlotKey)</c>.
        /// </summary>
        [Test]
        public void DefaultSlotKeyAliasesUntargetedHandleDefault()
        {
            SlotKey untargeted = new SlotKey(
                DispatchKind.Untargeted,
                DispatchPhase.Handle,
                DispatchVariant.Default
            );
            Assert.AreEqual(default(SlotKey), untargeted);
            Assert.AreEqual(0, default(SlotKey).Packed);
            Assert.AreNotEqual(SlotKey.None, default(SlotKey));
        }

        /// <summary>
        /// Asserts that no two real (non-<see cref="SlotKey.None"/>)
        /// registration methods collide on the same <see cref="SlotKey"/>.
        /// </summary>
        [Test]
        public void RealRegistrationMethodsMapToUniqueSlotKeys()
        {
            Dictionary<SlotKey, List<RegistrationMethod>> groups = new();
            foreach (RegistrationMethod method in EnumerateRegistrationMethods())
            {
                SlotKey key = RegistrationMethodAxes.GetSlotKey(method);
                if (key == SlotKey.None)
                {
                    continue;
                }
                if (!groups.TryGetValue(key, out List<RegistrationMethod> list))
                {
                    list = new List<RegistrationMethod>();
                    groups[key] = list;
                }
                list.Add(method);
            }

            foreach (KeyValuePair<SlotKey, List<RegistrationMethod>> entry in groups)
            {
                Assert.AreEqual(
                    1,
                    entry.Value.Count,
                    "SlotKey {0} is shared by multiple methods: {1}",
                    entry.Key,
                    string.Join(", ", entry.Value)
                );
            }
        }

        /// <summary>
        /// Asserts the only registration methods that map to
        /// <see cref="SlotKey.None"/> are
        /// <see cref="RegistrationMethod.GlobalAcceptAll"/> and
        /// <see cref="RegistrationMethod.Interceptor"/>.
        /// </summary>
        [Test]
        public void OnlySentinelMethodsMapToNone()
        {
            HashSet<RegistrationMethod> expectedSentinels = new()
            {
                RegistrationMethod.GlobalAcceptAll,
                RegistrationMethod.Interceptor,
            };

            foreach (RegistrationMethod method in EnumerateRegistrationMethods())
            {
                SlotKey key = RegistrationMethodAxes.GetSlotKey(method);
                bool mapsToNone = key == SlotKey.None;
                bool isSentinel = expectedSentinels.Contains(method);
                Assert.AreEqual(
                    isSentinel,
                    mapsToNone,
                    "Method {0} -> SlotKey.None mismatch (expected sentinel: {1}, actual mapsToNone: {2}).",
                    method,
                    isSentinel,
                    mapsToNone
                );
            }
        }

        /// <summary>
        /// Asserts the packed encoding of a representative <see cref="SlotKey"/>
        /// matches the documented bit layout and that decoded properties
        /// round-trip the constructor inputs.
        /// </summary>
        [Test]
        public void SlotKeyPackedEncodingIsCorrect()
        {
            SlotKey key = new SlotKey(
                DispatchKind.Broadcast,
                DispatchPhase.PostProcess,
                DispatchVariant.WithoutContext
            );
            int expectedPacked =
                ((int)DispatchKind.Broadcast << 4)
                | ((int)DispatchPhase.PostProcess << 3)
                | (int)DispatchVariant.WithoutContext;
            Assert.AreEqual((byte)expectedPacked, key.Packed);
            Assert.AreEqual(DispatchKind.Broadcast, key.Kind);
            Assert.AreEqual(DispatchPhase.PostProcess, key.Phase);
            Assert.AreEqual(DispatchVariant.WithoutContext, key.Variant);
        }

        /// <summary>
        /// Asserts <see cref="SlotKey.None"/> is distinct from
        /// <c>default(SlotKey)</c>, ensuring an unset
        /// <see cref="SlotKey"/> field is never accidentally interpreted as
        /// "no axis applies".
        /// </summary>
        [Test]
        public void SlotKeyNoneIsDistinctFromDefault()
        {
            SlotKey defaultKey = default;
            Assert.AreNotEqual(SlotKey.None, defaultKey);
            Assert.IsTrue(SlotKey.None != defaultKey);
            Assert.IsFalse(SlotKey.None == defaultKey);
            Assert.AreNotEqual(SlotKey.None.Packed, defaultKey.Packed);
        }

        /// <summary>
        /// Asserts the <see cref="SlotKey"/> constructor throws when an axis
        /// value exceeds its allotted bit width.
        /// </summary>
        [Test]
        public void SlotKeyOutOfRangeArgumentsThrow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                _ = new SlotKey((DispatchKind)16, DispatchPhase.Handle, DispatchVariant.Default)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                _ = new SlotKey(DispatchKind.Untargeted, (DispatchPhase)2, DispatchVariant.Default)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                _ = new SlotKey(DispatchKind.Untargeted, DispatchPhase.Handle, (DispatchVariant)8)
            );
        }

        /// <summary>
        /// Asserts the <see cref="SlotKey"/> constructor rejects the
        /// <c>(15, 1, 7)</c> triple, which would otherwise pack to
        /// <c>0xFF</c> -- the bit pattern reserved for
        /// <see cref="SlotKey.None"/>.
        /// </summary>
        [Test]
        public void SlotKeyConstructorRejectsNoneAlias()
        {
            Assert.Throws<ArgumentException>(() =>
                new SlotKey((DispatchKind)15, (DispatchPhase)1, (DispatchVariant)7)
            );
        }

        private static IEnumerable<RegistrationMethod> EnumerateRegistrationMethods()
        {
            return Enum.GetValues(typeof(RegistrationMethod)).Cast<RegistrationMethod>();
        }
    }
}
