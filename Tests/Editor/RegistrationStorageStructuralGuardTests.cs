#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    /// <summary>
    /// Deterministic structural guards for two registration-path allocation reductions on
    /// <see cref="MessageRegistrationToken"/>. Both assert a field/parameter TYPE via
    /// reflection -- they use no allocation probe, so they are deterministic and
    /// backend-independent and never flake in a warm editor.
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="InternalRegisterDerivesMetadataFromRegistration"/> pins metadata ownership
    /// on the unified registration object, without a second dictionary or call-site descriptor.
    /// </description></item>
    /// <item><description>
    /// <see cref="RegistrationsStoreUnifiedRegistrationObjectNotFuncOrAction"/> pins the
    /// unified per-handle Registration-object collapse: a revert to storing a staging
    /// <c>Func&lt;handle, HandlerDeregistration&gt;</c> (or a parameterless
    /// <see cref="Action"/> wrapper) re-introduces the staging delegate plus its display
    /// class AND the nested AugmentedHandler local-function delegate per registration.
    /// </description></item>
    /// <item><description>
    /// <see cref="RegistrationObjectsOwnReusableTeardownState"/> pins the common teardown
    /// state as value-type fields on the existing registration objects. Allocation-backed
    /// wrappers remain only for direct handler compatibility and overlapping retry spills.
    /// </description></item>
    /// </list>
    /// <para>
    /// These guards live in the per-PR EditMode CORRECTNESS leg
    /// (<c>WallstopStudios.DxMessaging.Tests.Editor</c>), NOT the weekly, perf-gated
    /// Allocation suite (<c>...Tests.Editor.Allocations</c>, which is <c>isPerf</c> and so
    /// excluded from per-PR runs). Because they are deterministic reflection assertions
    /// rather than <c>GC.Alloc</c>-recorder budgets, running them on every PR protects the
    /// two allocation wins continuously without any flake risk. The complementary
    /// allocation-COUNT budgets (which DO need the probe and the warm-editor denoising) stay
    /// in the Allocation suite; see
    /// <c>DxMessaging.Tests.Editor.Allocations.RegistrationAllocationCountTests</c>.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class RegistrationStorageStructuralGuardTests
    {
        /// <summary>
        /// Pins metadata derivation structurally: <c>InternalRegister</c> accepts only the
        /// unified registration object, whose <c>Metadata</c> property derives the diagnostic
        /// descriptor from its existing kind, context, message type, and priority fields.
        /// </summary>
        [Test]
        public void InternalRegisterDerivesMetadataFromRegistration()
        {
            MethodInfo internalRegister = typeof(MessageRegistrationToken).GetMethod(
                "InternalRegister",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                internalRegister,
                Is.Not.Null,
                "MessageRegistrationToken.InternalRegister was renamed; update this guard."
            );

            Assert.That(
                internalRegister.GetParameters(),
                Has.Length.EqualTo(1),
                "InternalRegister must not receive a duplicate metadata descriptor."
            );
            Type registrationType = internalRegister.GetParameters()[0].ParameterType;
            Assert.That(
                registrationType
                    .GetProperty(
                        "Metadata",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    )
                    ?.PropertyType,
                Is.EqualTo(typeof(MessageRegistrationMetadata)),
                "The unified Registration object must derive its own diagnostic metadata."
            );
        }

        /// <summary>
        /// Pins the unified per-handle Registration-object collapse structurally: the
        /// token's <c>_registrations</c> map must store ONE per-handle registration
        /// OBJECT (the abstract <c>Registration</c> reference type nested in
        /// <see cref="MessageRegistrationToken"/>), never a staging
        /// <c>Func&lt;MessageRegistrationHandle, MessageHandler.HandlerDeregistration&gt;</c>
        /// and never a parameterless <see cref="Action"/> wrapper. The pre-change form
        /// stored a staging <c>Func</c> (a delegate plus its display class capturing
        /// target/source/handler/priority) whose nested <c>AugmentedHandler</c> local
        /// function became a SECOND delegate handed to the bus. Collapsing both into a
        /// single Registration object (captured state as fields, the augmented invoker
        /// an instance method bound to the object) removes about two managed allocations
        /// per registration, uniformly across every registration kind, while keeping the
        /// hot dispatch path delegate-based. A revert to a <c>Func</c>/<c>Action</c> value
        /// type re-introduces those allocations. This assertion is deterministic and
        /// backend-independent, so it catches a revert even where the allocation probe is
        /// unavailable.
        /// </summary>
        [Test]
        public void RegistrationArenaStoresUnifiedRegistrationObjectAndOrderLinks()
        {
            FieldInfo slots = typeof(MessageRegistrationToken).GetField(
                "_slots",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                slots,
                Is.Not.Null,
                "MessageRegistrationToken._slots was renamed; update this guard."
            );

            Assert.That(slots.FieldType.IsArray, Is.True);
            Type slotType = slots.FieldType.GetElementType();
            Assert.That(slotType, Is.Not.Null);
            FieldInfo registration = slotType.GetField(
                "Registration",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            Assert.That(registration, Is.Not.Null);
            Type valueType = registration.FieldType;

            // The value must be the unified per-handle registration OBJECT, not a
            // delegate. A revert to either the staging Func or a parameterless Action
            // wrapper re-introduces the staging display class + delegate (and the
            // separate AugmentedHandler delegate) per registration.
            Assert.That(
                typeof(Delegate).IsAssignableFrom(valueType),
                Is.False,
                "MessageRegistrationToken.RegistrationSlot must store a per-handle registration "
                    + "OBJECT, not a delegate. A Func<MessageRegistrationHandle, "
                    + "MessageHandler.HandlerDeregistration> (or a parameterless Action wrapper) "
                    + "re-introduces the staging display class plus the staging delegate AND the "
                    + "nested AugmentedHandler delegate per registration."
            );
            Assert.That(
                valueType,
                Is.Not.EqualTo(
                    typeof(Func<MessageRegistrationHandle, MessageHandler.HandlerDeregistration>)
                ),
                "MessageRegistrationToken.RegistrationSlot must NOT store the staging Func."
            );
            Assert.That(
                valueType.IsClass && valueType.IsAbstract,
                Is.True,
                "MessageRegistrationToken.RegistrationSlot must store the abstract per-handle "
                    + "Registration base reference type (the unified staging object), so every "
                    + "registration kind shares the single collapsed object shape."
            );
            Assert.That(
                valueType.Name,
                Is.EqualTo("Registration"),
                "MessageRegistrationToken._registrations value type must be the nested "
                    + "'Registration' object; update this guard if it was renamed."
            );
            Assert.That(
                valueType.DeclaringType,
                Is.EqualTo(typeof(MessageRegistrationToken)),
                "The Registration object must remain nested in MessageRegistrationToken."
            );

            foreach (string linkName in new[] { "Previous", "Next", "NextFree" })
            {
                Assert.That(
                    slotType.GetField(
                        linkName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    ),
                    Is.Not.Null,
                    $"RegistrationSlot must retain its O(1) {linkName} link."
                );
            }
        }

        /// <summary>
        /// Pins the inline single-deregistration storage structurally: the token's
        /// <c>_deregistrations</c> map must store its value as <see cref="object"/>, so the
        /// common case (EXACTLY ONE de-registration per handle) stores the de-registration
        /// <see cref="Action"/> INLINE as the dictionary value with NO per-registration
        /// <c>PendingDeregistration</c> holder object. A <c>PendingDeregistration</c> is
        /// allocated only when a rare second de-registration accumulates on the same handle
        /// (retarget-recovery replay). A revert to a
        /// <c>Dictionary&lt;MessageRegistrationHandle, PendingDeregistration&gt;</c> value type
        /// re-introduces one holder allocation per registration; this deterministic,
        /// backend-independent assertion catches that revert even where the allocation probe is
        /// unavailable.
        /// </summary>
        [Test]
        public void RegistrationArenaStoresInlineDeregistrationNotPerHandleMap()
        {
            FieldInfo slots = typeof(MessageRegistrationToken).GetField(
                "_slots",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(slots, Is.Not.Null);
            Type slotType = slots.FieldType.GetElementType();
            FieldInfo deregistration = slotType.GetField(
                "Deregistration",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            Assert.That(
                deregistration,
                Is.Not.Null,
                "RegistrationSlot must own its live teardown state."
            );
            Assert.That(
                deregistration.FieldType,
                Is.EqualTo(typeof(object)),
                "RegistrationSlot.Deregistration must store its value as object so the "
                    + "single common-case de-registration Action is stored INLINE (no per-handle "
                    + "PendingDeregistration holder)."
            );
            Assert.That(
                typeof(MessageRegistrationToken).GetField(
                    "_deregistrations",
                    BindingFlags.Instance | BindingFlags.NonPublic
                ),
                Is.Null,
                "A parallel deregistration dictionary duplicates arena state."
            );
        }

        [Test]
        public void RegistrationObjectsOwnReusableTeardownState()
        {
            Type registration = typeof(MessageRegistrationToken).GetNestedType(
                "Registration",
                BindingFlags.NonPublic
            );
            Type typedRegistration = typeof(MessageRegistrationToken).GetNestedType(
                "Registration`1",
                BindingFlags.NonPublic
            );
            Type interceptorRegistration = typeof(MessageRegistrationToken).GetNestedType(
                "InterceptorRegistration`1",
                BindingFlags.NonPublic
            );
            Type globalRegistration = typeof(MessageRegistrationToken).GetNestedType(
                "GlobalAcceptAllRegistration",
                BindingFlags.NonPublic
            );

            Assert.That(registration, Is.Not.Null);
            Assert.That(
                typeof(MessageHandler.HandlerDeregistration).IsAssignableFrom(registration),
                Is.True,
                "The existing Registration object must execute its own common-case teardown."
            );

            FieldInfo typedState = typedRegistration?.GetField(
                "_typedDeregistration",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            FieldInfo interceptorState = interceptorRegistration?.GetField(
                "_deregistration",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            FieldInfo globalState = globalRegistration?.GetField(
                "_deregistration",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                typedState?.FieldType.IsValueType,
                Is.True,
                "Typed registrations must embed reusable teardown state rather than a second object."
            );
            Assert.That(
                interceptorState?.FieldType.IsValueType,
                Is.True,
                "Interceptor registrations must embed reusable teardown state rather than a second object."
            );
            Assert.That(
                globalState?.FieldType.IsValueType,
                Is.True,
                "Global accept-all registrations must embed their composite teardown state rather than a second object."
            );
            Assert.That(
                typedRegistration.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                Has.None.Matches<FieldInfo>(field =>
                    field.FieldType == typeof(MessageHandler.HandlerDeregistration)
                ),
                "The typed common path must not retain an allocation-backed teardown wrapper."
            );
            Assert.That(
                interceptorRegistration.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                Has.None.Matches<FieldInfo>(field =>
                    field.FieldType == typeof(MessageHandler.HandlerDeregistration)
                ),
                "The interceptor common path must not retain an allocation-backed teardown wrapper."
            );
            Assert.That(
                globalRegistration.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                Has.None.Matches<FieldInfo>(field =>
                    field.FieldType == typeof(MessageHandler.HandlerDeregistration)
                ),
                "The global accept-all common path must not retain an allocation-backed teardown wrapper."
            );

            Type globalStateType = globalState.FieldType;
            FieldInfo[] componentStates = globalStateType.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                componentStates,
                Has.Exactly(3)
                    .Matches<FieldInfo>(field =>
                        field.FieldType == typeof(MessageHandler.GlobalHandlerDeregistrationState)
                    ),
                "The composite global teardown must store three value sub-handler states, not Action wrappers."
            );
            Assert.That(
                componentStates,
                Has.None.Matches<FieldInfo>(field =>
                    typeof(Delegate).IsAssignableFrom(field.FieldType)
                ),
                "The composite global teardown must not retain delegate-backed teardown wrappers."
            );
        }

        [Test]
        public void RegistrationHandleStoresIdAndArenaSlotWithoutCachedHash()
        {
            FieldInfo id = typeof(MessageRegistrationHandle).GetField(
                "_id",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            FieldInfo slot = typeof(MessageRegistrationHandle).GetField(
                "_slot",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            FieldInfo cachedHash = typeof(MessageRegistrationHandle).GetField(
                "_hashCode",
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            Assert.That(id?.FieldType, Is.EqualTo(typeof(long)));
            Assert.That(slot?.FieldType, Is.EqualTo(typeof(int)));
            Assert.That(
                cachedHash,
                Is.Null,
                "The handle hash must be computed from id instead of duplicating the slot field."
            );
        }

        /// <summary>
        /// Pins the v4 opaque-handle contract structurally: every <see cref="IMessageBus"/>
        /// <c>Register*</c> method must return <see cref="MessageBusRegistration"/> (not
        /// <see cref="Action"/>), and the bus must expose a generic
        /// <see cref="IMessageBus.Deregister{T}"/>. A revert to returning <c>Action</c>
        /// re-introduces the per-registration bus deregistration closure (the Layer-A
        /// allocation this rework removed).
        /// </summary>
        [Test]
        public void AllBusRegisterMethodsReturnMessageBusRegistrationHandle()
        {
            int registerMethods = 0;
            foreach (
                MethodInfo method in typeof(IMessageBus).GetMethods(
                    BindingFlags.Public | BindingFlags.Instance
                )
            )
            {
                if (!method.Name.StartsWith("Register", StringComparison.Ordinal))
                {
                    continue;
                }

                registerMethods++;
                Assert.That(
                    method.ReturnType,
                    Is.EqualTo(typeof(MessageBusRegistration)),
                    $"IMessageBus.{method.Name} must return MessageBusRegistration (the v4 "
                        + "opaque handle); returning Action re-introduces the per-registration "
                        + "bus deregistration closure."
                );
            }

            Assert.That(
                registerMethods,
                Is.EqualTo(14),
                "Expected the 14 IMessageBus.Register* methods; update this guard if the "
                    + "registration surface changes."
            );

            MethodInfo deregister = typeof(IMessageBus).GetMethod(
                "Deregister",
                BindingFlags.Public | BindingFlags.Instance
            );
            Assert.That(
                deregister,
                Is.Not.Null,
                "IMessageBus must expose Deregister<T>(in MessageBusRegistration)."
            );
            Assert.That(
                deregister.IsGenericMethodDefinition,
                Is.True,
                "IMessageBus.Deregister must be generic in the message type T."
            );
            Assert.That(
                deregister.ReturnType,
                Is.EqualTo(typeof(void)),
                "IMessageBus.Deregister<T> must return void."
            );
        }
    }
}
#endif
