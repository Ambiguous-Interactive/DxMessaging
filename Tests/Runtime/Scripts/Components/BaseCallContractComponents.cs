#pragma warning disable RCS1242 // Fast handlers intentionally observe mutable messages by readonly reference.
namespace DxMessaging.Tests.Runtime.Scripts.Components
{
    using DxMessaging.Core.Attributes;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;

    /// <summary>
    /// Test fixtures that pin the runtime consequence of forgetting a
    /// <c>base.X()</c> call when subclassing <see cref="MessageAwareComponent"/>.
    /// Each component in this file deliberately violates the base-call contract
    /// for one specific lifecycle method so the tests in
    /// <c>BaseCallContractTests</c> can observe the runtime symptom directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every contract-violating class carries
    /// <see cref="DxIgnoreMissingBaseCallAttribute"/> at class scope so the
    /// Roslyn analyzer (DXMSG006/007/009/010) and the IL scanner do not
    /// surface warnings for these intentional violations. The attribute is
    /// the canonical opt-out for this exact pattern.
    /// </para>
    /// <para>
    /// The <see cref="CorrectBaseCallContractComponent"/> type is the positive
    /// control: every guarded lifecycle method is overridden correctly with
    /// a <c>base.X()</c> call so the contract test can pin the happy path
    /// end-to-end.
    /// </para>
    /// </remarks>
    public static class BaseCallContractComponents
    {
        // Marker class so the file has a public type matching the file name;
        // the actual test fixtures live as siblings below.
    }

    /// <summary>
    /// Subclass that overrides <see cref="MessageAwareComponent.Awake"/>
    /// without calling <c>base.Awake()</c>. The framework's token creation
    /// is therefore skipped and <c>_messageRegistrationToken</c> stays
    /// null, which is the failure mode the runtime self-check breadcrumb
    /// in <see cref="MessageAwareComponent.OnEnable"/> exists to detect.
    /// </summary>
    [DxIgnoreMissingBaseCall]
    public sealed class MissingBaseAwakeComponent : MessageAwareComponent
    {
        protected override void Awake()
        {
            // Intentional: do NOT call base.Awake().
        }
    }

    /// <summary>
    /// Subclass that overrides <see cref="MessageAwareComponent.OnEnable"/>
    /// without calling <c>base.OnEnable()</c>. The token is created by the
    /// untouched <c>Awake</c> but never enabled, so handlers registered on
    /// the token do not fire even when the component is enabled.
    /// </summary>
    [DxIgnoreMissingBaseCall]
    public sealed class MissingBaseOnEnableComponent : MessageAwareComponent
    {
        protected override void OnEnable()
        {
            // Intentional: do NOT call base.OnEnable().
        }
    }

    /// <summary>
    /// Subclass that overrides <see cref="MessageAwareComponent.OnDisable"/>
    /// without calling <c>base.OnDisable()</c>. The token is therefore not
    /// disabled when the component is disabled, so handlers continue to
    /// fire while the component appears to be off.
    /// </summary>
    [DxIgnoreMissingBaseCall]
    public sealed class MissingBaseOnDisableComponent : MessageAwareComponent
    {
        protected override void OnDisable()
        {
            // Intentional: do NOT call base.OnDisable().
        }
    }

    /// <summary>
    /// Subclass that overrides BOTH <see cref="MessageAwareComponent.OnDisable"/>
    /// and <see cref="MessageAwareComponent.OnDestroy"/> without calling either
    /// <c>base</c> method. Both must be skipped to demonstrate the on-destroy
    /// leak: Unity's destroy lifecycle fires <c>OnDisable</c> before
    /// <c>OnDestroy</c>, and the inherited <c>OnDisable</c> calls
    /// <c>_messageRegistrationToken?.Disable()</c> which deregisters every
    /// active registration. If only <c>OnDestroy</c> were skipped, the
    /// inherited <c>OnDisable</c> would silently clean up the registrations
    /// during destruction and the test would observe no leak. With both
    /// skipped, the framework never releases the messaging component or
    /// disables the token, so the registrations leak into the bus and the
    /// registration counters do not return to their pre-spawn baseline.
    /// </summary>
    [DxIgnoreMissingBaseCall]
    public sealed class MissingBaseOnDestroyComponent : MessageAwareComponent
    {
        protected override void OnDisable()
        {
            // Intentional: do NOT call base.OnDisable(). Without this skip,
            // Unity's destroy lifecycle (OnDisable -> OnDestroy) would
            // deregister the handlers via the inherited OnDisable before the
            // overridden OnDestroy runs, masking the leak the test pins.
        }

        protected override void OnDestroy()
        {
            // Intentional: do NOT call base.OnDestroy(). The leaked
            // registration is cleaned up by the test via a bus reset in
            // teardown.
        }
    }

    /// <summary>
    /// Subclass that overrides only <see cref="MessageAwareComponent.OnDestroy"/>
    /// without calling <c>base.OnDestroy()</c>, but leaves the inherited
    /// <see cref="MessageAwareComponent.OnDisable"/> intact. Used to pin that
    /// Unity's destroy lifecycle (OnDisable -> OnDestroy) deregisters handlers
    /// via the inherited OnDisable before the overridden OnDestroy runs, so
    /// no leak is observed even though base.OnDestroy() is skipped. This
    /// fixture exists to document the lifecycle interaction explicitly and
    /// distinguishes "skipping OnDestroy alone" (no leak) from "skipping both
    /// OnDisable and OnDestroy" (leak), the latter of which is modelled by
    /// <see cref="MissingBaseOnDestroyComponent"/>.
    /// </summary>
    [DxIgnoreMissingBaseCall]
    public sealed class MissingBaseOnDestroyOnlyComponent : MessageAwareComponent
    {
        protected override void OnDestroy()
        {
            // Intentional: do NOT call base.OnDestroy(). The inherited
            // base.OnDisable() still runs as part of Unity's destroy
            // lifecycle and disables the token, so no registration leaks.
        }
    }

    /// <summary>
    /// Subclass that overrides
    /// <see cref="MessageAwareComponent.RegisterMessageHandlers"/> without
    /// calling <c>base.RegisterMessageHandlers()</c>. The token is created
    /// (Awake is untouched) and the user's own handler is registered, but
    /// the default <c>StringMessage</c> / <c>GlobalStringMessage</c> handlers
    /// the base class normally registers are skipped.
    /// </summary>
    [DxIgnoreMissingBaseCall]
    public sealed class MissingBaseRegisterMessageHandlersComponent : MessageAwareComponent
    {
        public int defaultHandlerInvocations;
        public int userHandlerInvocations;

        protected override void RegisterMessageHandlers()
        {
            // Intentional: do NOT call base.RegisterMessageHandlers().
            _ = Token.RegisterUntargeted<SimpleUntargetedMessage>(HandleUserUntargeted);
        }

        protected override void HandleStringComponentMessage(in StringMessage message)
        {
            // The base class normally registers this as a handler. Without the
            // base call in RegisterMessageHandlers, this should never run for
            // emitted StringMessage instances during the test window.
            defaultHandlerInvocations++;
        }

        private void HandleUserUntargeted(in SimpleUntargetedMessage message)
        {
            userHandlerInvocations++;
        }
    }

    /// <summary>
    /// Positive-control subclass that overrides every guarded lifecycle
    /// method correctly (each one chains via <c>base.X()</c>). Used to pin
    /// the happy path for the base-call contract: handlers register, fire
    /// after enable, stop firing after disable, and deregister cleanly on
    /// destroy with no bus leak.
    /// </summary>
    public sealed class CorrectBaseCallContractComponent : MessageAwareComponent
    {
        public int userHandlerInvocations;

        protected override bool RegisterForStringMessages => false;

        protected override void Awake()
        {
            base.Awake();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
        }

        protected override void RegisterMessageHandlers()
        {
            base.RegisterMessageHandlers();
            _ = Token.RegisterUntargeted<SimpleUntargetedMessage>(HandleUserUntargeted);
        }

        private void HandleUserUntargeted(in SimpleUntargetedMessage message)
        {
            userHandlerInvocations++;
        }
    }
}
#pragma warning restore RCS1242
