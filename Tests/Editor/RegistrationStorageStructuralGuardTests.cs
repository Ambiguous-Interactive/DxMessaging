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
    /// <see cref="RegistrationsStoreStagingFunctionNotWrapperAction"/> pins the
    /// per-registration wrapper-closure collapse: a revert to storing a parameterless
    /// <see cref="Action"/> wrapper re-introduces a delegate plus its display class per
    /// registration.
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
        /// Pins the per-registration wrapper-closure collapse structurally: the token's
        /// <c>_registrations</c> map must store the staging function
        /// (<c>Func&lt;MessageRegistrationHandle, Action&gt;</c>) DIRECTLY, never a
        /// parameterless <see cref="Action"/>. The pre-change form stored a
        /// per-registration <c>Registration</c> wrapper local function (a delegate plus its
        /// display class, captured from <c>InternalRegister</c>) that only re-bundled the
        /// handle, the staging function, and the <c>AddDeregistration</c> call; storing the
        /// staging function directly and pairing it with its handle in the replay queue
        /// removes that delegate AND <c>InternalRegister</c>'s display class -- about two
        /// managed allocations per registration, uniformly across every registration kind
        /// (measured cold-total floor: Untargeted 14.69 -&gt; 12.69 allocs/registration, a
        /// clean -2.00). This assertion is deterministic and backend-independent, so it
        /// catches a revert even where the allocation probe is unavailable.
        /// </summary>
        [Test]
        public void RegistrationsStoreStagingFunctionNotWrapperAction()
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
                    + "MessageRegistrationHandle to the staging function."
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
            Assert.That(
                valueType,
                Is.EqualTo(typeof(Func<MessageRegistrationHandle, Action>)),
                "MessageRegistrationToken._registrations must store the staging function "
                    + "(Func<MessageRegistrationHandle, Action>) directly, not a per-registration "
                    + "Action wrapper. Wrapping the staging function in a parameterless Action "
                    + "re-introduces one delegate plus its display class allocation per "
                    + "registration (the collapsed 'Registration' local function)."
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
