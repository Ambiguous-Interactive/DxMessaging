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
    /// <see cref="InternalRegisterPassesMetadataByValueNotFactory"/> pins the by-value
    /// metadata change: a revert to a <c>Func&lt;MessageRegistrationMetadata&gt;</c> factory
    /// re-introduces one delegate allocation per registration.
    /// </description></item>
    /// <item><description>
    /// <see cref="RegistrationsStoreUnifiedRegistrationObjectNotFuncOrAction"/> pins the
    /// unified per-handle Registration-object collapse: a revert to storing a staging
    /// <c>Func&lt;handle, HandlerDeregistration&gt;</c> (or a parameterless
    /// <see cref="Action"/> wrapper) re-introduces the staging delegate plus its display
    /// class AND the nested AugmentedHandler local-function delegate per registration.
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
        /// Pins the by-value metadata change structurally: <c>InternalRegister</c>'s
        /// metadata parameter must be the <see cref="MessageRegistrationMetadata"/> struct,
        /// never a <c>Func&lt;MessageRegistrationMetadata&gt;</c>. The factory was invoked
        /// immediately (so it provided no laziness), and a revert to it re-introduces one
        /// delegate allocation per registration. This assertion is deterministic and
        /// backend-independent, so it catches that revert even where the allocation probe is
        /// unavailable.
        /// </summary>
        [Test]
        public void InternalRegisterPassesMetadataByValueNotFactory()
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

            ParameterInfo[] parameters = internalRegister.GetParameters();
            bool hasByValueMetadata = false;
            bool hasMetadataFactory = false;
            foreach (ParameterInfo parameter in parameters)
            {
                if (parameter.ParameterType == typeof(MessageRegistrationMetadata))
                {
                    hasByValueMetadata = true;
                }
                if (parameter.ParameterType == typeof(Func<MessageRegistrationMetadata>))
                {
                    hasMetadataFactory = true;
                }
            }

            Assert.That(
                hasByValueMetadata,
                Is.True,
                "InternalRegister must accept MessageRegistrationMetadata by value so no "
                    + "per-registration metadata closure is allocated."
            );
            Assert.That(
                hasMetadataFactory,
                Is.False,
                "InternalRegister must not accept a Func<MessageRegistrationMetadata>; the "
                    + "factory was invoked immediately, so it only added a closure allocation."
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
        public void RegistrationsStoreUnifiedRegistrationObjectNotFuncOrAction()
        {
            FieldInfo registrations = typeof(MessageRegistrationToken).GetField(
                "_registrations",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                registrations,
                Is.Not.Null,
                "MessageRegistrationToken._registrations was renamed; update this guard."
            );

            Type registrationMapType = registrations.FieldType;
            Assert.That(
                registrationMapType.IsGenericType,
                Is.True,
                "MessageRegistrationToken._registrations must remain a generic map from "
                    + "MessageRegistrationHandle to the unified per-handle registration object."
            );
            Assert.That(
                registrationMapType.GetGenericTypeDefinition(),
                Is.EqualTo(typeof(Dictionary<,>)),
                "MessageRegistrationToken._registrations must remain a Dictionary<,> so "
                    + "registration replay preserves the same storage and allocation contract."
            );

            Type[] registrationMapArguments = registrationMapType.GetGenericArguments();
            Assert.That(
                registrationMapArguments,
                Has.Length.EqualTo(2),
                "MessageRegistrationToken._registrations must have key and value generic "
                    + "arguments."
            );
            Assert.That(
                registrationMapArguments[0],
                Is.EqualTo(typeof(MessageRegistrationHandle)),
                "MessageRegistrationToken._registrations must be keyed by "
                    + "MessageRegistrationHandle."
            );

            Type valueType = registrationMapArguments[1];

            // The value must be the unified per-handle registration OBJECT, not a
            // delegate. A revert to either the staging Func or a parameterless Action
            // wrapper re-introduces the staging display class + delegate (and the
            // separate AugmentedHandler delegate) per registration.
            Assert.That(
                typeof(Delegate).IsAssignableFrom(valueType),
                Is.False,
                "MessageRegistrationToken._registrations must store a per-handle registration "
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
                "MessageRegistrationToken._registrations must NOT store the staging Func."
            );
            Assert.That(
                valueType.IsClass && valueType.IsAbstract,
                Is.True,
                "MessageRegistrationToken._registrations must store the abstract per-handle "
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
        public void DeregistrationsStoreInlineActionNotPerHandleHolder()
        {
            FieldInfo deregistrations = typeof(MessageRegistrationToken).GetField(
                "_deregistrations",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(
                deregistrations,
                Is.Not.Null,
                "MessageRegistrationToken._deregistrations was renamed; update this guard."
            );

            Type deregistrationMapType = deregistrations.FieldType;
            Assert.That(
                deregistrationMapType.IsGenericType
                    && deregistrationMapType.GetGenericTypeDefinition() == typeof(Dictionary<,>),
                Is.True,
                "MessageRegistrationToken._deregistrations must remain a Dictionary<,>."
            );

            Type[] arguments = deregistrationMapType.GetGenericArguments();
            Assert.That(
                arguments[0],
                Is.EqualTo(typeof(MessageRegistrationHandle)),
                "MessageRegistrationToken._deregistrations must be keyed by MessageRegistrationHandle."
            );
            Assert.That(
                arguments[1],
                Is.EqualTo(typeof(object)),
                "MessageRegistrationToken._deregistrations must store its value as object so the "
                    + "single common-case de-registration Action is stored INLINE (no per-handle "
                    + "PendingDeregistration holder). A Dictionary<..., PendingDeregistration> value "
                    + "type re-introduces one holder allocation per registration."
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
