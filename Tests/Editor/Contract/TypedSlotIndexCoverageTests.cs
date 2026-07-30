namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using NUnit.Framework;

    /// <summary>
    /// Contract guardrails for the per-message-type slot-index tables:
    /// <see cref="TypedSlotIndex"/> (20-slot
    /// <c>TypedSlot&lt;T&gt;[]</c> on <c>TypedHandler&lt;T&gt;</c>) and
    /// <see cref="TypedGlobalSlotIndex"/> (6-slot
    /// <c>TypedGlobalSlot[]</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each test pins one structural invariant typed storage depends on. The
    /// tests reflect over a freshly-instantiated typed handler
    /// rather than asserting against a hand-written shape so accidental
    /// future field additions / index renumbering surface here BEFORE they
    /// drift away from <c>ValidateSlotArrays()</c>.
    /// </para>
    /// <para>
    /// <c>TypedHandler&lt;T&gt;</c> is internal; the
    /// <c>InternalsVisibleTo</c> declarations on
    /// <c>WallstopStudios.DxMessaging.Tests.Editor</c> let the tests reach
    /// it directly.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("Contract")]
    public sealed class TypedSlotIndexCoverageTests
    {
        private readonly struct ProbeMessage : IUntargetedMessage { }

        private readonly struct ProbeTargetedMessage : ITargetedMessage { }

        // The expected legacy field name -> slot-index constant map. The names
        // stay here so new variants must still pick an explicit axis-indexed slot.
        private static readonly (string FieldName, string ConstantName)[] LegacySlotMap =
        {
            ("_untargetedHandlers", nameof(TypedSlotIndex.UntargetedHandleDefault)),
            ("_untargetedFastHandlers", nameof(TypedSlotIndex.UntargetedHandleFast)),
            (
                "_untargetedPostProcessingHandlers",
                nameof(TypedSlotIndex.UntargetedPostProcessDefault)
            ),
            (
                "_untargetedPostProcessingFastHandlers",
                nameof(TypedSlotIndex.UntargetedPostProcessFast)
            ),
            ("_targetedHandlers", nameof(TypedSlotIndex.TargetedHandleDefault)),
            ("_targetedFastHandlers", nameof(TypedSlotIndex.TargetedHandleFast)),
            (
                "_targetedWithoutTargetingHandlers",
                nameof(TypedSlotIndex.TargetedHandleWithoutContext)
            ),
            (
                "_fastTargetedWithoutTargetingHandlers",
                nameof(TypedSlotIndex.TargetedHandleWithoutContextFast)
            ),
            ("_targetedPostProcessingHandlers", nameof(TypedSlotIndex.TargetedPostProcessDefault)),
            ("_targetedPostProcessingFastHandlers", nameof(TypedSlotIndex.TargetedPostProcessFast)),
            (
                "_targetedWithoutTargetingPostProcessingHandlers",
                nameof(TypedSlotIndex.TargetedPostProcessWithoutContext)
            ),
            (
                "_fastTargetedWithoutTargetingPostProcessingHandlers",
                nameof(TypedSlotIndex.TargetedPostProcessWithoutContextFast)
            ),
            ("_broadcastHandlers", nameof(TypedSlotIndex.BroadcastHandleDefault)),
            ("_broadcastFastHandlers", nameof(TypedSlotIndex.BroadcastHandleFast)),
            (
                "_broadcastWithoutSourceHandlers",
                nameof(TypedSlotIndex.BroadcastHandleWithoutContext)
            ),
            (
                "_fastBroadcastWithoutSourceHandlers",
                nameof(TypedSlotIndex.BroadcastHandleWithoutContextFast)
            ),
            (
                "_broadcastPostProcessingHandlers",
                nameof(TypedSlotIndex.BroadcastPostProcessDefault)
            ),
            (
                "_broadcastPostProcessingFastHandlers",
                nameof(TypedSlotIndex.BroadcastPostProcessFast)
            ),
            (
                "_broadcastWithoutSourcePostProcessingHandlers",
                nameof(TypedSlotIndex.BroadcastPostProcessWithoutContext)
            ),
            (
                "_fastBroadcastWithoutSourcePostProcessingHandlers",
                nameof(TypedSlotIndex.BroadcastPostProcessWithoutContextFast)
            ),
        };

        private static readonly string[] LegacyGlobalFieldNames =
        {
            "_globalUntargetedHandlers",
            "_globalUntargetedFastHandlers",
            "_globalTargetedHandlers",
            "_globalTargetedFastHandlers",
            "_globalBroadcastHandlers",
            "_globalBroadcastFastHandlers",
        };

        // Dispatch links were deleted outright by the stage-3 flattening
        // cleanup (the resolved flat dispatch arrays replaced them), so
        // neither the legacy named link fields nor the interim
        // _dispatchLinks slot array may ever be redeclared.
        private static readonly string[] LegacyDispatchLinkFieldNames =
        {
            "_dispatchLinks",
            "_untargetedLink",
            "_untargetedPostLink",
            "_targetedLink",
            "_targetedPostLink",
            "_targetedWithoutTargetingLink",
            "_targetedWithoutTargetingPostLink",
            "_broadcastLink",
            "_broadcastPostLink",
            "_broadcastWithoutSourceLink",
            "_broadcastWithoutSourcePostLink",
        };

        [Test]
        public void SlotIndexLengthMatchesArrayLengthOnFreshTypedHandler()
        {
            object handler = MakeFreshTypedHandler();
            Array slots = ReadArrayField(handler, "_slots");
            Assert.AreEqual(
                TypedSlotIndex.Length,
                slots.Length,
                "_slots.Length must equal TypedSlotIndex.Length so every "
                    + "constant indexes a valid slot."
            );
        }

        [Test]
        public void GlobalSlotIndexLengthMatchesArrayLengthOnFreshTypedHandler()
        {
            object handler = MakeFreshTypedHandler();
            Array slots = ReadArrayField(handler, "_globalSlots");
            Assert.AreEqual(TypedGlobalSlotIndex.Length, slots.Length);
        }

        [Test]
        public void TypedSlotIndexConstantsAreUniqueAndContiguousZeroBased()
        {
            int[] values = ReadConstantValues(typeof(TypedSlotIndex));
            Assert.AreEqual(
                values.Length,
                values.Distinct().Count(),
                $"TypedSlotIndex constants must have unique values; got [{string.Join(",", values)}]"
            );
            int[] expected = Enumerable.Range(0, TypedSlotIndex.Length).ToArray();
            CollectionAssert.AreEqual(
                expected,
                values,
                "TypedSlotIndex constants (excluding Length) must be the "
                    + "contiguous zero-based range [0, Length)."
            );
        }

        [Test]
        public void TypedGlobalSlotIndexConstantsAreUniqueAndContiguousZeroBased()
        {
            int[] values = ReadConstantValues(typeof(TypedGlobalSlotIndex));
            Assert.AreEqual(
                values.Length,
                values.Distinct().Count(),
                $"TypedGlobalSlotIndex constants must have unique values; got [{string.Join(",", values)}]"
            );
            int[] expected = Enumerable.Range(0, TypedGlobalSlotIndex.Length).ToArray();
            CollectionAssert.AreEqual(expected, values);
        }

        [Test]
        public void AllTypedSlotsAreNullOnFreshTypedHandler()
        {
            object handler = MakeFreshTypedHandler();

            Array slots = ReadArrayField(handler, "_slots");
            for (int i = 0; i < slots.Length; ++i)
            {
                Assert.IsNull(
                    slots.GetValue(i),
                    "_slots["
                        + i
                        + "] must be null on a fresh TypedHandler<T>; slots populate lazily "
                        + "on first registration."
                );
            }

            Array globalSlots = ReadArrayField(handler, "_globalSlots");
            for (int i = 0; i < globalSlots.Length; ++i)
            {
                Assert.IsNull(globalSlots.GetValue(i), "_globalSlots[" + i + "] must be null.");
            }
        }

        [Test]
        public void TypedHandlerImplementsExternalSweepSurface()
        {
            object handler = MakeFreshTypedHandler();

            Assert.IsInstanceOf<ITypedHandlerSlotSweeper>(
                handler,
                "TypedHandler<T> must expose an erased sweep surface so "
                    + "MessageCache<object> callers can reset empty typed slots without reflection."
            );
        }

        [Test]
        public void LegacyNamedFieldsAreDeletedAfterSlotMigration()
        {
            Type typedHandlerOpen = typeof(MessageHandler).GetNestedType(
                "TypedHandler`1",
                BindingFlags.NonPublic
            );
            Assert.IsNotNull(
                typedHandlerOpen,
                "MessageHandler.TypedHandler<T> nested type must exist."
            );
            Type closed = typedHandlerOpen.MakeGenericType(typeof(ProbeMessage));

            FieldInfo[] declaredFields = closed.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            HashSet<string> declaredFieldNames = new(declaredFields.Select(f => f.Name));
            HashSet<string> indexConstantNames = new(
                typeof(TypedSlotIndex)
                    .GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(f =>
                        f.IsLiteral && !f.IsInitOnly && f.Name != nameof(TypedSlotIndex.Length)
                    )
                    .Select(f => f.Name)
            );

            Assert.AreEqual(
                LegacySlotMap.Length,
                indexConstantNames.Count,
                "TypedSlotIndex constant count drift detected. Update LegacySlotMap "
                    + "in this test file in lockstep with the TypedSlotIndex table."
            );
            Assert.AreEqual(
                TypedSlotIndex.Length,
                LegacySlotMap.Length,
                "LegacySlotMap row count must equal TypedSlotIndex.Length."
            );
            Assert.AreEqual(
                LegacySlotMap.Length,
                LegacySlotMap.Select(x => x.FieldName).Distinct().Count(),
                "LegacySlotMap must not duplicate FieldName."
            );
            Assert.AreEqual(
                LegacySlotMap.Length,
                LegacySlotMap.Select(x => x.ConstantName).Distinct().Count(),
                "LegacySlotMap must not duplicate ConstantName."
            );

            foreach ((string fieldName, string constantName) in LegacySlotMap)
            {
                Assert.IsFalse(
                    declaredFieldNames.Contains(fieldName),
                    "TypedHandler<T> must not redeclare legacy typed field '"
                        + fieldName
                        + "' because typed storage owns that slot."
                );
                Assert.IsTrue(
                    indexConstantNames.Contains(constantName),
                    "TypedSlotIndex must declare constant '" + constantName + "'."
                );
                FieldInfo constant = typeof(TypedSlotIndex).GetField(
                    constantName,
                    BindingFlags.Public | BindingFlags.Static
                );
                Assert.AreEqual(
                    Array.IndexOf(LegacySlotMap, (fieldName, constantName)),
                    (int)constant.GetRawConstantValue(),
                    "TypedSlotIndex." + constantName + " must keep its documented numeric slot."
                );
            }

            foreach (string fieldName in LegacyGlobalFieldNames)
            {
                Assert.IsFalse(
                    declaredFieldNames.Contains(fieldName),
                    "TypedHandler<T> must not redeclare legacy global field '"
                        + fieldName
                        + "' because typed global storage owns that slot."
                );
            }

            foreach (string fieldName in LegacyDispatchLinkFieldNames)
            {
                Assert.IsFalse(
                    declaredFieldNames.Contains(fieldName),
                    "TypedHandler<T> must not redeclare legacy dispatch-link field '"
                        + fieldName
                        + "' because typed dispatch storage owns that slot."
                );
            }
        }

        [Test]
        public void UntargetedRegistrationTracksLiveCountAndOrderedPriorities()
        {
            object handler = MakeFreshTypedHandler();
            Type handlerType = handler.GetType();
            MethodInfo addMethod = handlerType.GetMethod(
                "AddUntargetedHandler",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[]
                {
                    typeof(Action<ProbeMessage>),
                    typeof(Action<ProbeMessage>),
                    typeof(MessageBusRegistration),
                    typeof(int),
                    typeof(IMessageBus),
                },
                modifiers: null
            );
            Assert.IsNotNull(addMethod, "AddUntargetedHandler(Action<T>) must exist.");

            MessageBus bus = new MessageBus();
            Action<ProbeMessage> original = _ => { };
            Action<ProbeMessage> augmented = _ => { };
            Action firstDeregistration = ToDeregistrationAction(
                addMethod.Invoke(handler, new object[] { original, augmented, null, 17, bus })
            );
            Action secondDeregistration = ToDeregistrationAction(
                addMethod.Invoke(handler, new object[] { original, augmented, null, 17, bus })
            );
            Action<ProbeMessage> otherOriginal = _ => { };
            Action distinctDeregistration = ToDeregistrationAction(
                addMethod.Invoke(handler, new object[] { otherOriginal, augmented, null, 19, bus })
            );

            Array slots = ReadArrayField(handler, "_slots");
            object populated = slots.GetValue(TypedSlotIndex.UntargetedHandleDefault);
            Assert.IsNotNull(populated, "Untargeted default registration must populate its slot.");
            TypedSlot<ProbeMessage> slot = (TypedSlot<ProbeMessage>)populated;
            Assert.IsFalse(slot.requiresContext);
            Assert.AreEqual(2, slot.liveCount);
            Assert.IsTrue(slot.byPriority.ContainsKey(17));
            Assert.IsTrue(slot.byPriority.ContainsKey(19));
            CollectionAssert.AreEqual(new[] { 17, 19 }, slot.orderedPriorities);

            Assert.IsNull(slots.GetValue(TypedSlotIndex.UntargetedHandleFast));
            Assert.IsNull(slots.GetValue(TypedSlotIndex.TargetedHandleDefault));

            firstDeregistration();
            Assert.AreEqual(
                2,
                slot.liveCount,
                "Partial deregistration of a duplicate handler must not decrement liveCount."
            );
            secondDeregistration();
            Assert.AreEqual(1, slot.liveCount);
            secondDeregistration();
            Assert.AreEqual(1, slot.liveCount, "Over-deregistration must not underflow liveCount.");
            distinctDeregistration();
            Assert.AreEqual(0, slot.liveCount);
            Assert.IsTrue(slot.IsEmpty);
        }

        [Test]
        public void ExternalSweepResetsEmptyUntargetedSlotAndInvalidatesStaleDeregistration()
        {
            DxMessaging.Core.MessageBus.MessageBus bus =
                new DxMessaging.Core.MessageBus.MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(0x5033_0101), bus)
            {
                active = true,
            };
            Action<ProbeMessage> original = _ => { };
            Action deregistration = handler.RegisterUntargetedMessageHandler(
                original,
                original,
                priority: 17,
                messageBus: bus
            );
            object typedHandler = ReadTypedHandler<ProbeMessage>(handler, bus);
            Array slots = ReadArrayField(typedHandler, "_slots");
            TypedSlot<ProbeMessage> slot =
                (TypedSlot<ProbeMessage>)slots.GetValue(TypedSlotIndex.UntargetedHandleDefault);

            deregistration();
            long versionBeforeReset = slot.version;
            int resetCount = handler.ResetEmptyTypedSlotsForSweep(bus);

            Assert.AreEqual(1, resetCount);
            Assert.IsNull(slots.GetValue(TypedSlotIndex.UntargetedHandleDefault));
            Assert.Greater(
                slot.version,
                versionBeforeReset,
                "External sweep must call Reset(), not Clear(), so stale deregistration "
                    + "closures observe a monotonic slot version bump."
            );

            Action newDeregistration = handler.RegisterUntargetedMessageHandler(
                original,
                original,
                priority: 17,
                messageBus: bus
            );
            object newTypedHandler = ReadTypedHandler<ProbeMessage>(handler, bus);
            Assert.AreNotSame(
                typedHandler,
                newTypedHandler,
                "External sweep removes an empty typed-handler wrapper, so a later "
                    + "registration must allocate a replacement wrapper."
            );
            Array newSlots = ReadArrayField(newTypedHandler, "_slots");
            TypedSlot<ProbeMessage> newSlot =
                (TypedSlot<ProbeMessage>)newSlots.GetValue(TypedSlotIndex.UntargetedHandleDefault);
            Assert.AreEqual(1, newSlot.liveCount);

            deregistration();
            Assert.AreEqual(
                1,
                newSlot.liveCount,
                "A stale deregistration captured before external sweep must not affect "
                    + "a later registration that reused the same typed handler."
            );

            newDeregistration();
            Assert.AreEqual(0, newSlot.liveCount);
        }

        [Test]
        public void ExternalSweepPreservesActiveUntargetedSlotAndDispatch()
        {
            DxMessaging.Core.MessageBus.MessageBus bus =
                new DxMessaging.Core.MessageBus.MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(0x5033_0102), bus)
            {
                active = true,
            };
            int handled = 0;
            Action<ProbeMessage> callback = _ => handled++;
            Action deregistration = handler.RegisterUntargetedMessageHandler(
                callback,
                callback,
                priority: 17,
                messageBus: bus
            );
            object typedHandler = ReadTypedHandler<ProbeMessage>(handler, bus);
            Array slots = ReadArrayField(typedHandler, "_slots");
            TypedSlot<ProbeMessage> slot =
                (TypedSlot<ProbeMessage>)slots.GetValue(TypedSlotIndex.UntargetedHandleDefault);

            int resetCount = handler.ResetEmptyTypedSlotsForSweep(bus);
            ProbeMessage message = new ProbeMessage();
            bus.UntargetedBroadcast(ref message);

            Assert.AreEqual(0, resetCount);
            Assert.AreSame(slot, slots.GetValue(TypedSlotIndex.UntargetedHandleDefault));
            Assert.AreEqual(1, slot.liveCount);
            Assert.AreEqual(1, handled);

            deregistration();
        }

        [Test]
        public void ContextRegistrationReturnsContextDictionariesToPoolsOnReset()
        {
            _ = DxPools.TrimAll(force: true);

            object handler = MakeFreshTypedHandler();
            Type handlerType = handler.GetType();
            MethodInfo addMethod = handlerType.GetMethod(
                "AddTargetedHandler",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[]
                {
                    typeof(InstanceId),
                    typeof(Action<ProbeMessage>),
                    typeof(Action<ProbeMessage>),
                    typeof(MessageBusRegistration),
                    typeof(int),
                    typeof(IMessageBus),
                },
                modifiers: null
            );
            Assert.IsNotNull(addMethod, "AddTargetedHandler(Action<T>) must exist.");

            MessageBus bus = new MessageBus();
            InstanceId target = new InstanceId(0x5033_0001);
            Action<ProbeMessage> original = _ => { };
            Action<ProbeMessage> augmented = _ => { };
            _ = addMethod.Invoke(
                handler,
                new object[] { target, original, augmented, null, 17, bus }
            );

            Array slots = ReadArrayField(handler, "_slots");
            TypedSlot<ProbeMessage> slot =
                (TypedSlot<ProbeMessage>)slots.GetValue(TypedSlotIndex.TargetedHandleDefault);
            Assert.IsNotNull(slot);
            Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> outer = slot.byContext;
            Dictionary<int, IHandlerActionCache> inner = outer[target];

            slot.Reset();
            Assert.IsNull(slot.byContext);

            Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> rentedAgainOuter =
                DxPools.TypedHandlerContextDicts.Rent();
            Dictionary<int, IHandlerActionCache> rentedAgainInner =
                DxPools.TypedHandlerPriorityDicts.Rent();
            try
            {
                Assert.AreSame(outer, rentedAgainOuter);
                Assert.AreSame(inner, rentedAgainInner);
            }
            finally
            {
                DxPools.TypedHandlerContextDicts.Return(rentedAgainOuter);
                DxPools.TypedHandlerPriorityDicts.Return(rentedAgainInner);
            }
        }

        [Test]
        public void ExternalSweepResetsEmptyContextSlotAndReturnsDictionariesToPools()
        {
            _ = DxPools.TrimAll(force: true);

            DxMessaging.Core.MessageBus.MessageBus bus =
                new DxMessaging.Core.MessageBus.MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(0x5033_0103), bus)
            {
                active = true,
            };
            InstanceId target = new InstanceId(0x5033_0104);
            Action<ProbeTargetedMessage> original = _ => { };
            Action deregistration = handler.RegisterTargetedMessageHandler(
                target,
                original,
                original,
                priority: 17,
                messageBus: bus
            );
            object typedHandler = ReadTypedHandler<ProbeTargetedMessage>(handler, bus);
            Array slots = ReadArrayField(typedHandler, "_slots");
            TypedSlot<ProbeTargetedMessage> slot =
                (TypedSlot<ProbeTargetedMessage>)
                    slots.GetValue(TypedSlotIndex.TargetedHandleDefault);
            Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> outer = slot.byContext;
            Dictionary<int, IHandlerActionCache> inner = outer[target];

            deregistration();
            int resetCount = handler.ResetEmptyTypedSlotsForSweep(bus);

            Assert.AreEqual(1, resetCount);
            Assert.IsNull(slots.GetValue(TypedSlotIndex.TargetedHandleDefault));
            Assert.IsNull(slot.byContext);

            Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> rentedAgainOuter =
                DxPools.TypedHandlerContextDicts.Rent();
            Dictionary<int, IHandlerActionCache> rentedAgainInner =
                DxPools.TypedHandlerPriorityDicts.Rent();
            try
            {
                Assert.AreSame(outer, rentedAgainOuter);
                Assert.AreSame(inner, rentedAgainInner);
            }
            finally
            {
                DxPools.TypedHandlerContextDicts.Return(rentedAgainOuter);
                DxPools.TypedHandlerPriorityDicts.Return(rentedAgainInner);
            }
        }

        [Test]
        public void StaleContextDeregistrationAfterResetDoesNotTouchPooledDictionaryReuse()
        {
            _ = DxPools.TrimAll(force: true);

            MethodInfo addMethod = FindTargetedActionRegisterMethod(
                MakeFreshTypedHandler().GetType()
            );
            InstanceId target = new InstanceId(0x5033_0002);
            MessageBus bus = new MessageBus();
            Action<ProbeMessage> original = _ => { };
            Action<ProbeMessage> augmented = _ => { };

            object oldHandler = MakeFreshTypedHandler();
            Action oldDeregistration = ToDeregistrationAction(
                addMethod.Invoke(
                    oldHandler,
                    new object[] { target, original, augmented, null, 17, bus }
                )
            );
            TypedSlot<ProbeMessage> oldSlot =
                (TypedSlot<ProbeMessage>)
                    ReadArrayField(oldHandler, "_slots")
                        .GetValue(TypedSlotIndex.TargetedHandleDefault);
            oldSlot.Reset();

            object newHandler = MakeFreshTypedHandler();
            Action newDeregistration = ToDeregistrationAction(
                addMethod.Invoke(
                    newHandler,
                    new object[] { target, original, augmented, null, 17, bus }
                )
            );
            TypedSlot<ProbeMessage> newSlot =
                (TypedSlot<ProbeMessage>)
                    ReadArrayField(newHandler, "_slots")
                        .GetValue(TypedSlotIndex.TargetedHandleDefault);
            Assert.AreEqual(1, newSlot.liveCount);

            oldDeregistration();
            Assert.AreEqual(
                1,
                newSlot.liveCount,
                "A stale deregistration captured before Reset() must not remove handlers "
                    + "from a later slot that rented the same dictionaries."
            );

            newDeregistration();
            Assert.AreEqual(0, newSlot.liveCount);
            Assert.IsTrue(newSlot.IsEmpty);
        }

        [Test]
        public void GlobalRegistrationPopulatesExpectedGlobalSlot()
        {
            object handler = MakeFreshTypedHandler(typeof(IMessage));
            Type handlerType = handler.GetType();
            MethodInfo addMethod = handlerType.GetMethod(
                "AddGlobalUntargetedHandler",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[]
                {
                    typeof(Action<IUntargetedMessage>),
                    typeof(Action<IUntargetedMessage>),
                    typeof(IMessageBus),
                },
                modifiers: null
            );
            Assert.IsNotNull(
                addMethod,
                "AddGlobalUntargetedHandler(Action<IUntargetedMessage>) must exist."
            );

            MessageBus bus = new MessageBus();
            Action<IUntargetedMessage> original = _ => { };
            Action<IUntargetedMessage> augmented = _ => { };
            Action deregistration = ToDeregistrationAction(
                addMethod.Invoke(handler, new object[] { original, augmented, bus })
            );

            Array globalSlots = ReadArrayField(handler, "_globalSlots");
            object populated = globalSlots.GetValue(TypedGlobalSlotIndex.UntargetedDefault);
            Assert.IsNotNull(
                populated,
                "Global untargeted default registration must populate its slot."
            );
            TypedGlobalSlot slot = (TypedGlobalSlot)populated;
            Assert.AreEqual(1, slot.liveCount);
            Assert.IsNotNull(slot.cache);

            Assert.IsNull(globalSlots.GetValue(TypedGlobalSlotIndex.UntargetedFast));
            Assert.IsNull(globalSlots.GetValue(TypedGlobalSlotIndex.TargetedDefault));

            deregistration();
            Assert.AreEqual(0, slot.liveCount);
            Assert.IsTrue(slot.IsEmpty);
        }

        [Test]
        public void ExternalSweepResetsEmptyGlobalSlots()
        {
            DxMessaging.Core.MessageBus.MessageBus bus =
                new DxMessaging.Core.MessageBus.MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(0x5033_0105), bus)
            {
                active = true,
            };
            Action<IUntargetedMessage> untargeted = _ => { };
            Action<InstanceId, ITargetedMessage> targeted = (_, _) => { };
            Action<InstanceId, IBroadcastMessage> broadcast = (_, _) => { };
            Action deregistration = handler.RegisterGlobalAcceptAll(
                untargeted,
                untargeted,
                targeted,
                targeted,
                broadcast,
                broadcast,
                messageBus: bus
            );
            object typedHandler = ReadTypedHandler<IMessage>(handler, bus);
            Array globalSlots = ReadArrayField(typedHandler, "_globalSlots");
            TypedGlobalSlot untargetedSlot = (TypedGlobalSlot)
                globalSlots.GetValue(TypedGlobalSlotIndex.UntargetedDefault);
            TypedGlobalSlot targetedSlot = (TypedGlobalSlot)
                globalSlots.GetValue(TypedGlobalSlotIndex.TargetedDefault);
            TypedGlobalSlot broadcastSlot = (TypedGlobalSlot)
                globalSlots.GetValue(TypedGlobalSlotIndex.BroadcastDefault);

            deregistration();
            int resetCount = handler.ResetEmptyTypedSlotsForSweep(bus);

            Assert.AreEqual(3, resetCount);
            Assert.IsNull(globalSlots.GetValue(TypedGlobalSlotIndex.UntargetedDefault));
            Assert.IsNull(globalSlots.GetValue(TypedGlobalSlotIndex.TargetedDefault));
            Assert.IsNull(globalSlots.GetValue(TypedGlobalSlotIndex.BroadcastDefault));
            Assert.Greater(untargetedSlot.version, 0);
            Assert.Greater(targetedSlot.version, 0);
            Assert.Greater(broadcastSlot.version, 0);
        }

        [Test]
        public void ExternalSweepInvalidatesStaleTypedGlobalDeregistration()
        {
            object typedHandler = MakeFreshTypedHandler(typeof(IMessage));
            Type handlerType = typedHandler.GetType();
            MethodInfo addMethod = handlerType.GetMethod(
                "AddGlobalUntargetedHandler",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[]
                {
                    typeof(Action<IUntargetedMessage>),
                    typeof(Action<IUntargetedMessage>),
                    typeof(IMessageBus),
                },
                modifiers: null
            );
            Assert.IsNotNull(
                addMethod,
                "AddGlobalUntargetedHandler(Action<IUntargetedMessage>) must exist."
            );
            MessageBus bus = new MessageBus();
            Action<IUntargetedMessage> original = _ => { };
            Action<IUntargetedMessage> augmented = _ => { };
            Action staleDeregistration = ToDeregistrationAction(
                addMethod.Invoke(typedHandler, new object[] { original, augmented, bus })
            );
            Array globalSlots = ReadArrayField(typedHandler, "_globalSlots");

            staleDeregistration();
            int resetCount = ((ITypedHandlerSlotSweeper)typedHandler).ResetEmptySlotsForSweep();
            Assert.AreEqual(1, resetCount);
            Assert.IsNull(globalSlots.GetValue(TypedGlobalSlotIndex.UntargetedDefault));

            Action newDeregistration = ToDeregistrationAction(
                addMethod.Invoke(typedHandler, new object[] { original, augmented, bus })
            );
            TypedGlobalSlot newUntargetedSlot = (TypedGlobalSlot)
                globalSlots.GetValue(TypedGlobalSlotIndex.UntargetedDefault);
            Assert.AreEqual(1, newUntargetedSlot.liveCount);

            staleDeregistration();
            Assert.AreEqual(
                1,
                newUntargetedSlot.liveCount,
                "A stale global deregistration captured before external sweep must not "
                    + "decrement a later TypedGlobalSlot registration."
            );

            newDeregistration();
            Assert.AreEqual(0, newUntargetedSlot.liveCount);
        }

        private static object MakeFreshTypedHandler()
        {
            return MakeFreshTypedHandler(typeof(ProbeMessage));
        }

        private static object MakeFreshTypedHandler(Type messageType)
        {
            Type typedHandlerOpen = typeof(MessageHandler).GetNestedType(
                "TypedHandler`1",
                BindingFlags.NonPublic
            );
            Assert.IsNotNull(
                typedHandlerOpen,
                "MessageHandler.TypedHandler<T> nested type must exist."
            );
            Type closed = typedHandlerOpen.MakeGenericType(messageType);
            return Activator.CreateInstance(closed, nonPublic: true);
        }

        private static MethodInfo FindTargetedActionRegisterMethod(Type handlerType)
        {
            MethodInfo addMethod = handlerType.GetMethod(
                "AddTargetedHandler",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[]
                {
                    typeof(InstanceId),
                    typeof(Action<ProbeMessage>),
                    typeof(Action<ProbeMessage>),
                    typeof(MessageBusRegistration),
                    typeof(int),
                    typeof(IMessageBus),
                },
                modifiers: null
            );
            Assert.IsNotNull(addMethod, "AddTargetedHandler(Action<T>) must exist.");
            return addMethod;
        }

        private static Action ToDeregistrationAction(object state)
        {
            Assert.IsNotNull(state, "Registration must return teardown state.");
            MethodInfo deregister = state
                .GetType()
                .GetMethod("Deregister", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(deregister, "Teardown state must expose Deregister internally.");
            return () => deregister.Invoke(state, null);
        }

        private static Array ReadArrayField(object handler, string name)
        {
            FieldInfo field = handler
                .GetType()
                .GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
            Assert.IsNotNull(field, "TypedHandler<T> must declare field '" + name + "'.");
            object value = field.GetValue(handler);
            Assert.IsNotNull(value, "TypedHandler<T>.{0} must be non-null on construction.", name);
            return (Array)value;
        }

        private static object ReadTypedHandler<TMessage>(MessageHandler handler, IMessageBus bus)
            where TMessage : IMessage
        {
            Assert.Less(
                bus.RegisteredGlobalSequentialIndex,
                handler._handlersByTypeByMessageBus.Count
            );
            bool exists = handler
                ._handlersByTypeByMessageBus[bus.RegisteredGlobalSequentialIndex]
                .TryGetValue<TMessage>(out object typedHandler);
            Assert.IsTrue(exists, "Typed handler for " + typeof(TMessage).Name + " must exist.");
            Assert.IsNotNull(typedHandler);
            return typedHandler;
        }

        private static int[] ReadConstantValues(Type indexType)
        {
            return indexType
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.Name != "Length")
                .Select(f => (int)f.GetRawConstantValue())
                .OrderBy(v => v)
                .ToArray();
        }
    }
}
