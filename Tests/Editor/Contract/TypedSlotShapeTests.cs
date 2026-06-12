namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.MessageBus.Internal;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Contract guardrails for the typed-handler-side slot grid:
    /// <see cref="IHandlerActionCache"/> (the non-generic
    /// erasure of the per-delegate-shape cache type),
    /// <see cref="TypedSlot{T}"/> (the per-message-type, per-<see cref="SlotKey"/>
    /// dispatch slot), and <see cref="TypedGlobalSlot"/> (the per-message-type
    /// accept-all slot).
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests pin the eviction-driven monotonic <c>Reset()</c> contract
    /// and the <see cref="IEvictableSlot"/> shape that typed storage relies on.
    /// Each test below covers one structural invariant in the per-slot
    /// lifecycle.
    /// </para>
    /// <para>
    /// The <see cref="IHandlerActionCacheInterfaceShape"/> test reflects over
    /// the interface's declared members and is the structural backstop for
    /// typed storage. Any silent member add or remove here will fail this test
    /// until reviewers update both the interface and its expected-shape list in
    /// lockstep.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("Contract")]
    public sealed class TypedSlotShapeTests
    {
        // Centralized string-named members so reviewers update them in one
        // place when production renames land.
        private const string HandlerActionCacheNestedName = "HandlerActionCache`1";
        private const string EntryNestedName = "Entry";
        private const string EntriesFieldName = "entries";
        private const string CacheFieldName = "cache";
        private const string CountFieldName = "count";

        private readonly struct ProbeMessage : IUntargetedMessage { }

        // Fixture-private rename-stable probe for the open-vs-closed
        // regression test below. Pinning the .NET reflection rule against
        // this fixture-owned type (instead of the production
        // HandlerActionCache.Entry) keeps the regression backstop alive
        // even if the production type is renamed or restructured.
        private sealed class ProbeOuter<T>
        {
            internal readonly struct ProbeSlot
            {
                public ProbeSlot(T value, int count)
                {
                    this.value = value;
                    this.count = count;
                }

                public readonly T value;
                public readonly int count;
            }
        }

        // Probe shape for the non-generic-outer-with-generic-nested
        // diagnostic test.
        private sealed class NonGenericProbeOuter
        {
            internal readonly struct GenericProbeSlot<U>
            {
                public GenericProbeSlot(U value)
                {
                    this.value = value;
                }

                public readonly U value;
            }
        }

        // Probe shape for the HIGH-severity test that the helper rejects
        // nested types declaring their own generic parameters under the
        // three-arg overload, and accepts them under the four-arg overload.
        private sealed class ProbeOuterWithOwnEntryArg<T>
        {
            internal readonly struct OwnEntry<U>
            {
                public OwnEntry(T outerValue, U ownValue)
                {
                    this.outerValue = outerValue;
                    this.ownValue = ownValue;
                }

                public readonly T outerValue;
                public readonly U ownValue;
            }
        }

        /// <summary>
        /// Trivial in-test stub for <see cref="IHandlerActionCache"/>. Used so
        /// the slot tests can populate <see cref="TypedSlot{T}.byPriority"/>
        /// and <see cref="TypedSlot{T}.byContext"/> without depending on the
        /// real <c>HandlerActionCache&lt;TDelegate&gt;</c> implementation.
        /// </summary>
        private sealed class StubCache : IHandlerActionCache
        {
            public long Version { get; set; }

            public long LastSeenVersion { get; set; } = -1;

            public long LastSeenEmissionId { get; set; }

            public bool IsEmpty { get; set; } = true;

            public int ResetCallCount { get; private set; }

            public void Reset()
            {
                ++ResetCallCount;
            }
        }

        /// <summary>
        /// Probe stub for the drain-BEFORE-clear ordering tests.
        /// <see cref="ProbeOuterCount"/> is invoked from
        /// <see cref="Reset"/> and the observed value pinned in
        /// <see cref="ObservedOuterCountAtReset"/>; the slot tests set
        /// <see cref="ProbeOuterCount"/> to read the size of the outer
        /// container so a non-zero observation proves the inner drain ran
        /// while the outer dict still held the entry.
        /// </summary>
        private sealed class OrderingProbeCache : IHandlerActionCache
        {
            public Func<int> ProbeOuterCount = () => -1;
            public int ObservedOuterCountAtReset = -2;

            public long Version => 0;

            public long LastSeenVersion { get; set; }

            public long LastSeenEmissionId { get; set; }

            public bool IsEmpty => true;

            public void Reset()
            {
                ObservedOuterCountAtReset = ProbeOuterCount();
            }
        }

        [Test]
        public void TypedSlotConstructorRespectsRequiresContextFlag()
        {
            TypedSlot<ProbeMessage> contextless = new TypedSlot<ProbeMessage>(
                requiresContext: false
            );
            Assert.IsFalse(contextless.requiresContext);

            TypedSlot<ProbeMessage> withContext = new TypedSlot<ProbeMessage>(
                requiresContext: true
            );
            Assert.IsTrue(withContext.requiresContext);
        }

        [Test]
        public void TypedSlotIsEmptyWhenLiveCountZero()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: false);
            Assert.AreEqual(0, slot.liveCount);
            Assert.IsTrue(slot.IsEmpty);

            slot.liveCount = 1;
            Assert.IsFalse(slot.IsEmpty);

            slot.liveCount = 0;
            Assert.IsTrue(slot.IsEmpty);
        }

        [Test]
        public void TypedSlotResetBumpsVersionMonotonically()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: false);
            long previous = slot.version;
            for (int i = 0; i < 8; ++i)
            {
                slot.Reset();
                Assert.Greater(
                    slot.version,
                    previous,
                    "Reset() must bump version strictly monotonically."
                );
                previous = slot.version;
            }
        }

        [Test]
        public void TypedSlotResetPreservesLastTouchTicks()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: false);
            slot.lastTouchTicks = 42;
            slot.Reset();
            Assert.AreEqual(
                42,
                slot.lastTouchTicks,
                "Reset() must preserve lastTouchTicks so the sweep can distinguish "
                    + "freshly-reset slots from never-touched slots."
            );
        }

        /// <summary>
        /// Pins the per-cache drain: <see cref="TypedSlot{T}.Reset"/>
        /// must invoke <see cref="IHandlerActionCache.Reset"/> on every
        /// <see cref="IHandlerActionCache"/> held by
        /// <see cref="TypedSlot{T}.byPriority"/> BEFORE clearing the
        /// container. Drain order is load-bearing so that closures captured
        /// against the inner cache also detect invalidation.
        /// </summary>
        [Test]
        public void TypedSlotResetDrainsHeldCachesViaIHandlerActionCache()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: false);
            StubCache child = new StubCache();
            slot.byPriority[0] = child;

            slot.Reset();

            Assert.AreEqual(
                1,
                child.ResetCallCount,
                "Reset() must drain every IHandlerActionCache held by byPriority "
                    + "via IHandlerActionCache.Reset() BEFORE the structural clear. Re-check the xmldoc on "
                    + "TypedSlot<T>.Reset() if this assertion needs to change."
            );
        }

        /// <summary>
        /// Companion to <see cref="TypedSlotResetDrainsHeldCachesViaIHandlerActionCache"/>:
        /// pins that every inner cache held by
        /// <see cref="TypedSlot{T}.byContext"/> is also drained on
        /// <see cref="TypedSlot{T}.Reset"/>. Walks both axes of the flat
        /// 3-level <c>InstanceId -&gt; (priority -&gt; IHandlerActionCache)</c>
        /// shape used by typed storage.
        /// </summary>
        [Test]
        public void TypedSlotResetDrainsByContextHeldCachesViaIHandlerActionCache()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: true);
            StubCache a = new StubCache();
            StubCache b = new StubCache();
            slot.byContext = new Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>>
            {
                {
                    new InstanceId(1),
                    new Dictionary<int, IHandlerActionCache> { { 0, a } }
                },
                {
                    new InstanceId(2),
                    new Dictionary<int, IHandlerActionCache> { { 5, b } }
                },
            };

            slot.Reset();

            Assert.AreEqual(1, a.ResetCallCount);
            Assert.AreEqual(1, b.ResetCallCount);
        }

        /// <summary>
        /// Pins the drain-BEFORE-clear ordering on
        /// <see cref="TypedSlot{T}.byPriority"/>.
        /// The probe cache reads <c>byPriority.Count</c> at the moment its
        /// <see cref="IHandlerActionCache.Reset"/> fires; a value of 1
        /// proves the drain ran while the outer dict still held the entry.
        /// A value of 0 would indicate the outer clear ran first.
        /// </summary>
        [Test]
        public void TypedSlotResetDrainsBeforeClearingByPriority()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: false);
            OrderingProbeCache probe = new OrderingProbeCache();
            probe.ProbeOuterCount = () => slot.byPriority.Count;
            slot.byPriority[0] = probe;

            slot.Reset();

            Assert.AreEqual(
                1,
                probe.ObservedOuterCountAtReset,
                "Reset() must drain inner caches BEFORE clearing byPriority."
            );
        }

        /// <summary>
        /// Companion to <see cref="TypedSlotResetDrainsBeforeClearingByPriority"/>:
        /// pins the drain-BEFORE-clear ordering on
        /// <see cref="TypedSlot{T}.byContext"/>. The probe reads
        /// <c>byContext.Count</c> from inside its
        /// <see cref="IHandlerActionCache.Reset"/>; a value of 1 proves the
        /// drain ran while the outer dict still held the entry.
        /// </summary>
        [Test]
        public void TypedSlotResetDrainsBeforeClearingByContext()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: true);
            OrderingProbeCache probe = new OrderingProbeCache();
            probe.ProbeOuterCount = () => slot.byContext.Count;
            slot.byContext = new Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>>
            {
                {
                    new InstanceId(1),
                    new Dictionary<int, IHandlerActionCache> { { 0, probe } }
                },
            };

            slot.Reset();

            Assert.AreEqual(
                1,
                probe.ObservedOuterCountAtReset,
                "Reset() must drain inner caches BEFORE clearing byContext."
            );
        }

        /// <summary>
        /// Pins that <c>MessageHandler.HandlerActionCache&lt;T&gt;</c> implements
        /// <see cref="IHandlerActionCache"/>. The interface
        /// is implemented explicitly so the public-facing field shape on the
        /// nested cache type is unchanged; this test exercises the five
        /// interface members through an interface-typed reference to confirm
        /// they all dispatch without exception.
        /// </summary>
        [Test]
        public void HandlerActionCacheImplementsIHandlerActionCache()
        {
            System.Type nested = typeof(DxMessaging.Core.MessageHandler).GetNestedType(
                HandlerActionCacheNestedName,
                BindingFlags.NonPublic
            );
            Assert.IsNotNull(
                nested,
                "MessageHandler.HandlerActionCache<T> nested type must exist."
            );
            System.Type closed = nested.MakeGenericType(typeof(System.Action<int>));
            Assert.IsTrue(
                typeof(IHandlerActionCache).IsAssignableFrom(closed),
                "HandlerActionCache<T> must implement IHandlerActionCache."
            );

            object instance = System.Activator.CreateInstance(closed, nonPublic: true);
            IHandlerActionCache view = (IHandlerActionCache)instance;

            // Exercise every interface member; failure indicates a misapplied
            // explicit-interface implementation or accidental shadowing.
            long _ = view.Version;
            view.LastSeenVersion = 7;
            Assert.AreEqual(7, view.LastSeenVersion);
            Assert.AreEqual(
                -1,
                view.LastSeenEmissionId,
                "Fresh HandlerActionCache<T> instances must start with an invalid "
                    + "emission sentinel so emission id 0 materializes the first snapshot."
            );
            view.LastSeenEmissionId = 13;
            Assert.AreEqual(13, view.LastSeenEmissionId);
            Assert.IsTrue(
                view.IsEmpty,
                "Freshly-constructed HandlerActionCache<T> must report IsEmpty == true."
            );

            // Pre-populate entries + cache via reflection so Reset() has
            // observable inner state to drain. Both fields are public
            // readonly; reflection over the closed generic returns the same
            // collection instances the cache holds, so direct mutation
            // populates the cache.
            FieldInfo entriesField = closed.GetField(
                EntriesFieldName,
                BindingFlags.Public | BindingFlags.Instance
            );
            FieldInfo cacheField = closed.GetField(
                CacheFieldName,
                BindingFlags.Public | BindingFlags.Instance
            );
            Assert.IsNotNull(entriesField, "HandlerActionCache<T>.entries must exist.");
            Assert.IsNotNull(cacheField, "HandlerActionCache<T>.cache must exist.");
            System.Collections.IDictionary entries = (System.Collections.IDictionary)
                entriesField.GetValue(instance);
            System.Collections.IList cacheList = (System.Collections.IList)
                cacheField.GetValue(instance);
            // Entry is a struct nested inside HandlerActionCache<T> that uses
            // T as a field type. Per .NET reflection rules, GetNestedType
            // invoked on a closed outer generic returns the OPEN nested type
            // whenever the nested type uses the outer's generic parameters
            // (ContainsGenericParameters == true); it must be re-closed with
            // the outer's generic arguments before construction or
            // Activator.CreateInstance throws ArgumentException. The
            // CloseNestedGeneric helper centralizes that handshake; the
            // companion test EntryNestedTypeRetainsGenericParameterFromOuter
            // pins the underlying behavior so future refactors do not regress
            // back to the naive direct-Activator pattern.
            System.Type entryType = ReflectionHelpers.CloseNestedGeneric(
                closed,
                EntryNestedName,
                BindingFlags.NonPublic
            );
            Assert.IsNotNull(entryType, "HandlerActionCache<T>.Entry nested type must exist.");
            Assert.IsFalse(
                entryType.ContainsGenericParameters,
                "Entry must be fully closed before Activator.CreateInstance; "
                    + "ReflectionHelpers.CloseNestedGeneric is responsible for closing it."
            );
            System.Action<int> handler = _ignored => { };
            object entry = System.Activator.CreateInstance(entryType, handler, 1);
            entries[handler] = entry;
            cacheList.Add(handler);
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(1, cacheList.Count);
            Assert.IsFalse(
                view.IsEmpty,
                "After populating entries the cache must report IsEmpty == false."
            );

            long beforeReset = view.Version;
            view.Reset();
            Assert.Greater(view.Version, beforeReset, "Reset() must bump version monotonically.");
            Assert.AreEqual(-1, view.LastSeenVersion, "Reset() must restore lastSeenVersion = -1.");
            Assert.AreEqual(
                -1,
                view.LastSeenEmissionId,
                "Reset() must restore lastSeenEmissionId to an invalid sentinel so "
                    + "emission id 0 still materializes a fresh snapshot."
            );
            Assert.AreEqual(0, entries.Count, "Reset() must empty entries.");
            Assert.AreEqual(0, cacheList.Count, "Reset() must empty cache.");
            Assert.IsTrue(view.IsEmpty, "After Reset() the cache must report IsEmpty == true.");
        }

        /// <summary>
        /// Regression backstop for the
        /// <see cref="HandlerActionCacheImplementsIHandlerActionCache"/> test
        /// and for <see cref="ReflectionHelpers.CloseNestedGeneric"/>. Pins
        /// the .NET reflection rule that bit the original test: when a nested
        /// type uses one of its outer's generic parameters,
        /// <see cref="System.Type.GetNestedType(string, BindingFlags)"/>
        /// invoked on the closed outer returns the OPEN nested type (its
        /// <see cref="System.Type.ContainsGenericParameters"/> is still
        /// <c>true</c>) and must be re-closed with the outer's generic
        /// arguments via <see cref="System.Type.MakeGenericType(System.Type[])"/>
        /// before <see cref="System.Activator.CreateInstance(System.Type)"/>
        /// will succeed. The probe deliberately uses the fixture-private
        /// <see cref="ProbeOuter{T}.ProbeSlot"/> rather than the production
        /// nested type so this canary remains intact across renames or
        /// restructures of <c>HandlerActionCache.Entry</c>.
        /// </summary>
        [Test]
        public void EntryNestedTypeRetainsGenericParameterFromOuter()
        {
            System.Type closed = typeof(ProbeOuter<>).MakeGenericType(typeof(System.Action<int>));

            System.Type openSlot = closed.GetNestedType(
                nameof(ProbeOuter<int>.ProbeSlot),
                BindingFlags.NonPublic
            );
            Assert.IsNotNull(openSlot, "ProbeOuter<T>.ProbeSlot nested type must exist.");
            Assert.IsTrue(
                openSlot.ContainsGenericParameters,
                "GetNestedType on a closed outer generic returns the OPEN nested type "
                    + "when the nested type uses the outer's generic parameter; reviewers "
                    + "must close it with MakeGenericType(closed.GetGenericArguments()) "
                    + "before constructing instances."
            );
            // Type-specific Assert.Throws<ArgumentException> is intentional:
            // this is the early-warning canary for the .NET reflection rule
            // the helper exists to navigate. If a future runtime ever
            // changes the exception type or wraps it, the failure message
            // here surfaces the regression at the source rather than
            // letting it silently propagate through CloseNestedGeneric.
            Assert.Throws<System.ArgumentException>(
                () => System.Activator.CreateInstance(openSlot, (System.Action<int>)(x => { }), 1),
                "Activator.CreateInstance must reject the OPEN nested type so the "
                    + "open-vs-closed mistake fails loudly instead of silently constructing."
            );

            System.Type closedSlot = ReflectionHelpers.CloseNestedGeneric(
                closed,
                nameof(ProbeOuter<int>.ProbeSlot),
                BindingFlags.NonPublic
            );
            Assert.IsFalse(
                closedSlot.ContainsGenericParameters,
                "ReflectionHelpers.CloseNestedGeneric must produce a fully-closed nested type."
            );
            object slot = System.Activator.CreateInstance(
                closedSlot,
                (System.Action<int>)(x => { }),
                3
            );
            Assert.IsNotNull(
                slot,
                "Activator.CreateInstance must succeed on the closed nested type."
            );
            FieldInfo countField = closedSlot.GetField(
                CountFieldName,
                BindingFlags.Public | BindingFlags.Instance
            );
            Assert.IsNotNull(countField, "ProbeSlot.count field must exist.");
            Assert.AreEqual(3, countField.GetValue(slot));
        }

        /// <summary>
        /// Pins the contract for
        /// <see cref="ReflectionHelpers.CloseNestedGeneric(System.Type, string, BindingFlags)"/>:
        /// passing <c>null</c> for the outer type throws
        /// <see cref="System.ArgumentNullException"/> with a parameter-named
        /// message. Refactors that drop the explicit null check fail this
        /// test instead of surfacing a generic <see cref="System.NullReferenceException"/>
        /// at the call site.
        /// </summary>
        [Test]
        public void CloseNestedGenericRejectsNullOuter()
        {
            System.ArgumentNullException ex = Assert.Throws<System.ArgumentNullException>(() =>
                ReflectionHelpers.CloseNestedGeneric(null, EntryNestedName, BindingFlags.NonPublic)
            );
            StringAssert.Contains("closedOuter", ex.Message);
        }

        /// <summary>
        /// Pins the contract for
        /// <see cref="ReflectionHelpers.CloseNestedGeneric(System.Type, string, BindingFlags)"/>:
        /// passing <c>null</c> for the nested name throws
        /// <see cref="System.ArgumentNullException"/> with a parameter-named
        /// message. Pairs with <see cref="CloseNestedGenericRejectsNullOuter"/>.
        /// </summary>
        [Test]
        public void CloseNestedGenericRejectsNullName()
        {
            System.Type closed = typeof(ProbeOuter<>).MakeGenericType(typeof(System.Action<int>));
            System.ArgumentNullException ex = Assert.Throws<System.ArgumentNullException>(() =>
                ReflectionHelpers.CloseNestedGeneric(closed, null, BindingFlags.NonPublic)
            );
            StringAssert.Contains("nestedName", ex.Message);
        }

        /// <summary>
        /// Pins that
        /// <see cref="ReflectionHelpers.CloseNestedGeneric(System.Type, string, BindingFlags)"/>
        /// rejects an OPEN outer generic type (one whose
        /// <see cref="System.Type.ContainsGenericParameters"/> is still
        /// <c>true</c>). The diagnostic message must call out the
        /// "fully-closed outer" requirement so reviewers do not have to
        /// trace through the helper to understand the failure.
        /// </summary>
        [Test]
        public void CloseNestedGenericRejectsOpenOuter()
        {
            System.Type openOuter = typeof(ProbeOuter<>);
            System.InvalidOperationException ex = Assert.Throws<System.InvalidOperationException>(
                () =>
                    ReflectionHelpers.CloseNestedGeneric(
                        openOuter,
                        nameof(ProbeOuter<int>.ProbeSlot),
                        BindingFlags.NonPublic
                    )
            );
            StringAssert.Contains("fully-closed outer", ex.Message);
        }

        /// <summary>
        /// Pins that
        /// <see cref="ReflectionHelpers.CloseNestedGeneric(System.Type, string, BindingFlags)"/>
        /// throws a descriptive
        /// <see cref="System.InvalidOperationException"/> -- not a silent
        /// <c>null</c> -- when the requested nested name does not exist.
        /// The message must include both the missing name and the outer's
        /// fully-qualified name so reviewers can resolve typos quickly.
        /// </summary>
        [Test]
        public void CloseNestedGenericRejectsMissingNestedName()
        {
            System.Type closed = typeof(ProbeOuter<>).MakeGenericType(typeof(System.Action<int>));
            System.InvalidOperationException ex = Assert.Throws<System.InvalidOperationException>(
                () =>
                    ReflectionHelpers.CloseNestedGeneric(
                        closed,
                        "DoesNotExist",
                        BindingFlags.NonPublic
                    )
            );
            StringAssert.Contains("DoesNotExist", ex.Message);
            StringAssert.Contains("not found", ex.Message);
        }

        /// <summary>
        /// Pins that
        /// <see cref="ReflectionHelpers.CloseNestedGeneric(System.Type, string, BindingFlags)"/>
        /// throws <see cref="System.InvalidOperationException"/> -- never a
        /// silent <see cref="System.ArgumentException"/> from
        /// <c>MakeGenericType</c> -- when asked to close a generic nested
        /// type whose outer is non-generic (so there are no inherited
        /// generic arguments to supply).
        /// </summary>
        [Test]
        public void CloseNestedGenericRejectsGenericNestedOnNonGenericOuter()
        {
            System.InvalidOperationException ex = Assert.Throws<System.InvalidOperationException>(
                () =>
                    ReflectionHelpers.CloseNestedGeneric(
                        typeof(NonGenericProbeOuter),
                        nameof(NonGenericProbeOuter.GenericProbeSlot<int>) + "`1",
                        BindingFlags.NonPublic
                    )
            );
            StringAssert.Contains("non-generic", ex.Message);
        }

        /// <summary>
        /// Pins the HIGH-severity contract for the three-argument overload
        /// of <see cref="ReflectionHelpers.CloseNestedGeneric(System.Type, string, BindingFlags)"/>:
        /// when the nested type declares its OWN generic parameters in
        /// addition to those inherited from the outer, the helper must
        /// throw <see cref="System.InvalidOperationException"/> with a
        /// message directing the caller to the four-argument overload.
        /// Without this guard the helper would forward only the outer's
        /// arguments to <c>MakeGenericType</c> and surface a raw
        /// <see cref="System.ArgumentException"/> from the runtime instead.
        /// The companion test
        /// <see cref="CloseNestedGenericFourArgOverloadAcceptsNestedOwnArgs"/>
        /// covers the success path on the same shape.
        /// </summary>
        [Test]
        public void CloseNestedGenericRejectsNestedTypeWithOwnGenericParameters()
        {
            System.Type closed = typeof(ProbeOuterWithOwnEntryArg<>).MakeGenericType(
                typeof(System.Action<int>)
            );
            System.InvalidOperationException ex = Assert.Throws<System.InvalidOperationException>(
                () =>
                    ReflectionHelpers.CloseNestedGeneric(
                        closed,
                        nameof(ProbeOuterWithOwnEntryArg<int>.OwnEntry<int>) + "`1",
                        BindingFlags.NonPublic
                    )
            );
            StringAssert.Contains("of its own", ex.Message);
            StringAssert.Contains("overload", ex.Message);
        }

        /// <summary>
        /// Companion success path for
        /// <see cref="CloseNestedGenericRejectsNestedTypeWithOwnGenericParameters"/>:
        /// the four-argument overload accepts the explicit
        /// <c>nestedOwnArgs</c> and produces a fully-closed type whose
        /// inherited slot is taken from the outer and whose own slot is
        /// taken from the supplied argument array.
        /// </summary>
        [Test]
        public void CloseNestedGenericFourArgOverloadAcceptsNestedOwnArgs()
        {
            System.Type closed = typeof(ProbeOuterWithOwnEntryArg<>).MakeGenericType(
                typeof(System.Action<int>)
            );
            System.Type closedNested = ReflectionHelpers.CloseNestedGeneric(
                closed,
                nameof(ProbeOuterWithOwnEntryArg<int>.OwnEntry<int>) + "`1",
                BindingFlags.NonPublic,
                new System.Type[] { typeof(string) }
            );
            Assert.IsFalse(
                closedNested.ContainsGenericParameters,
                "Four-arg overload must produce a fully-closed nested type."
            );
            System.Type[] genericArgs = closedNested.GetGenericArguments();
            Assert.AreEqual(2, genericArgs.Length);
            Assert.AreEqual(typeof(System.Action<int>), genericArgs[0]);
            Assert.AreEqual(typeof(string), genericArgs[1]);
        }

        /// <summary>
        /// Pins that <see cref="TypedSlot{T}.byContext"/> is the flat
        /// 3-level <c>Dictionary&lt;InstanceId, Dictionary&lt;int, IHandlerActionCache&gt;&gt;</c>
        /// shape used by typed storage.
        /// </summary>
        [Test]
        public void TypedSlotByContextShapeIsFlatThreeLevelDictionary()
        {
            FieldInfo field = typeof(TypedSlot<ProbeMessage>).GetField(
                "byContext",
                BindingFlags.Public | BindingFlags.Instance
            );
            Assert.IsNotNull(field, "TypedSlot<T>.byContext field must exist.");
            System.Type expected = typeof(Dictionary<
                InstanceId,
                Dictionary<int, IHandlerActionCache>
            >);
            Assert.AreEqual(
                expected,
                field.FieldType,
                "TypedSlot<T>.byContext must be the flat 3-level shape "
                    + "Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>>."
            );
        }

        [Test]
        public void TypedSlotResetClearsByPriorityAndOrderedPriorities()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: false);
            slot.byPriority[0] = new StubCache();
            slot.byPriority[5] = new StubCache();
            slot.orderedPriorities.Add(0);
            slot.orderedPriorities.Add(5);

            slot.Reset();

            Assert.AreEqual(0, slot.byPriority.Count);
            Assert.AreEqual(0, slot.orderedPriorities.Count);
        }

        [Test]
        public void TypedSlotResetNullsOutByContext()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: true);
            slot.byContext = new Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>>
            {
                {
                    new InstanceId(1),
                    new Dictionary<int, IHandlerActionCache> { { 0, new StubCache() } }
                },
                {
                    new InstanceId(2),
                    new Dictionary<int, IHandlerActionCache> { { 0, new StubCache() } }
                },
            };

            slot.Reset();

            Assert.IsNull(
                slot.byContext,
                "Reset() must null out byContext after returning typed-handler-side context "
                    + "dictionaries to DxPools."
            );
        }

        [Test]
        public void TypedSlotClearResetsVersionToZero()
        {
            TypedSlot<ProbeMessage> slot = new TypedSlot<ProbeMessage>(requiresContext: false);
            // Drive version above zero via repeated Reset() so the test does
            // not depend on internal field write access for setup.
            slot.Reset();
            slot.Reset();
            slot.Reset();
            Assert.Greater(slot.version, 0);

            slot.Clear();

            Assert.AreEqual(
                0,
                slot.version,
                "Clear() is the legacy 'full reset' semantic and must reset "
                    + "version to 0; eviction-driven monotonicity belongs to Reset()."
            );
            Assert.AreEqual(-1, slot.lastSeenVersion);
            Assert.AreEqual(-1, slot.lastSeenEmissionId);
            Assert.AreEqual(0, slot.liveCount);
            Assert.AreEqual(0, slot.byPriority.Count);
            Assert.AreEqual(0, slot.orderedPriorities.Count);
            Assert.IsNull(slot.byContext);
        }

        [Test]
        public void TypedGlobalSlotResetBumpsVersionMonotonically()
        {
            TypedGlobalSlot slot = new TypedGlobalSlot();
            long previous = slot.version;
            for (int i = 0; i < 8; ++i)
            {
                slot.Reset();
                Assert.Greater(
                    slot.version,
                    previous,
                    "Reset() must bump version strictly monotonically."
                );
                previous = slot.version;
            }
        }

        [Test]
        public void TypedGlobalSlotIsEmptyWhenLiveCountZero()
        {
            TypedGlobalSlot slot = new TypedGlobalSlot();
            Assert.AreEqual(0, slot.liveCount);
            Assert.IsTrue(slot.IsEmpty);

            slot.liveCount = 3;
            Assert.IsFalse(slot.IsEmpty);

            slot.liveCount = 0;
            Assert.IsTrue(slot.IsEmpty);
        }

        [Test]
        public void TypedGlobalSlotResetClearsCache()
        {
            TypedGlobalSlot slot = new TypedGlobalSlot();
            slot.cache = new StubCache();
            Assert.IsNotNull(slot.cache);

            slot.Reset();

            Assert.IsNull(slot.cache);
        }

        [Test]
        public void TypedGlobalSlotResetPreservesLastTouchTicks()
        {
            TypedGlobalSlot slot = new TypedGlobalSlot();
            slot.lastTouchTicks = 99;
            slot.Reset();
            Assert.AreEqual(99, slot.lastTouchTicks);
        }

        [Test]
        public void TypedGlobalSlotClearResetsVersionToZero()
        {
            TypedGlobalSlot slot = new TypedGlobalSlot();
            slot.Reset();
            slot.Reset();
            Assert.Greater(slot.version, 0);

            slot.Clear();

            Assert.AreEqual(0, slot.version);
            Assert.AreEqual(-1, slot.lastSeenVersion);
            Assert.AreEqual(-1, slot.lastSeenEmissionId);
            Assert.AreEqual(0, slot.liveCount);
            Assert.IsNull(slot.cache);
        }

        [Test]
        public void TypedSlotImplementsIEvictableSlot()
        {
            Assert.IsTrue(
                typeof(IEvictableSlot).IsAssignableFrom(typeof(TypedSlot<ProbeMessage>)),
                "TypedSlot<T> must implement IEvictableSlot so the sweep can reclaim it."
            );
        }

        [Test]
        public void TypedGlobalSlotImplementsIEvictableSlot()
        {
            Assert.IsTrue(
                typeof(IEvictableSlot).IsAssignableFrom(typeof(TypedGlobalSlot)),
                "TypedGlobalSlot must implement IEvictableSlot so the sweep can reclaim it."
            );
        }

        /// <summary>
        /// Reflection-based shape pin for <see cref="IHandlerActionCache"/>.
        /// Asserts the interface declares exactly the five members the staged
        /// dispatch + eviction layers require: <see cref="IHandlerActionCache.Version"/>,
        /// <see cref="IHandlerActionCache.LastSeenVersion"/>,
        /// <see cref="IHandlerActionCache.LastSeenEmissionId"/>,
        /// <see cref="IHandlerActionCache.IsEmpty"/>, and
        /// <see cref="IHandlerActionCache.Reset"/>. Adding or removing a
        /// member breaks this test until reviewers update the expected list,
        /// providing a structural backstop for the
        /// <c>HandlerActionCache&lt;TDelegate&gt;</c> interface implementation.
        /// </summary>
        [Test]
        public void IHandlerActionCacheInterfaceShape()
        {
            string[] expected =
            {
                "Version",
                "LastSeenVersion",
                "LastSeenEmissionId",
                "IsEmpty",
                "Reset",
            };

            // GetMembers on an interface reports declared members directly.
            // Property accessors and event accessors are filtered out by
            // selecting only properties + methods that are NOT special-name.
            string[] actual = typeof(IHandlerActionCache)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m =>
                    m.MemberType == MemberTypes.Property
                    || (m.MemberType == MemberTypes.Method && !((MethodInfo)m).IsSpecialName)
                )
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            string[] sortedExpected = expected.OrderBy(n => n, StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(
                sortedExpected,
                actual,
                "IHandlerActionCache must expose exactly the documented member set. "
                    + "Adding or removing a member requires updating both the interface "
                    + "and this test in lockstep."
            );
        }
    }
}
