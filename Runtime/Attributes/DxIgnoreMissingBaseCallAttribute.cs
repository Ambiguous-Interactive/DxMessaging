namespace DxMessaging.Core.Attributes
{
    using System;

    /// <summary>
    /// Suppresses the <c>MessageAwareComponentBaseCallAnalyzer</c> (DXMSG006/DXMSG007/DXMSG009/DXMSG010)
    /// for the annotated class or method.
    /// </summary>
    /// <remarks>
    /// Applying this attribute is the source-level opt-out for the base-call analyzer. When applied to
    /// a class, every guarded lifecycle method on that class (<c>Awake</c>, <c>OnEnable</c>,
    /// <c>OnDisable</c>, <c>OnDestroy</c>, <c>RegisterMessageHandlers</c>) is exempt. When applied to a
    /// single method, only that method is exempt. The analyzer still emits an Info-level
    /// <c>DXMSG008</c> at the suppression site so the opt-out is auditable.
    /// <para>
    /// <c>Inherited = false</c>: a base class's <c>[DxIgnoreMissingBaseCall]</c> does NOT silently
    /// suppress derived classes. Each subclass must opt out explicitly.
    /// </para>
    /// </remarks>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Method,
        Inherited = false,
        AllowMultiple = false
    )]
    public sealed class DxIgnoreMissingBaseCallAttribute : Attribute { }
}
