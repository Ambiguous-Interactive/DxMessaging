namespace DxMessaging.Core.Attributes
{
    using System;

    /// <summary>
    /// Instructs the source generator to synthesize a constructor matching the fields on the type.
    /// </summary>
    /// <remarks>
    /// Apply this to a partial <c>struct</c> or <c>class</c> to generate a constructor whose parameters
    /// match public instance fields in declaration order. Fields marked with
    /// <see cref="DxOptionalParameterAttribute"/> are emitted as optional parameters.
    /// Excluded struct storage receives type defaults while existing field and auto-property initializers
    /// are preserved. For types without a primary constructor, the generated constructor does not
    /// invoke a user parameterless constructor.
    /// When compiling before C# 11, DXMSG011 reports mixed initialized and uninitialized explicit-interface
    /// auto-properties when a user parameterless constructor prevents preserving their initializers.
    /// DXMSG012 reports primary constructors with parameters: remove this attribute and write an explicit
    /// constructor with a this(...) initializer to choose the primary constructor arguments.
    /// An empty primary parameter list is chained automatically with this().
    /// A type with no public instance fields needs no generated constructor and is not diagnosed.
    /// DXMSG013 reports a record class whose sole public instance field has its own record type.
    /// Use a property with an object initializer or a different constructor signature to preserve copying.
    /// </remarks>
    /// <example>
    /// <code>
    /// [DxMessaging.Core.Attributes.DxUntargetedMessage]
    /// [DxMessaging.Core.Attributes.DxAutoConstructor]
    /// public readonly partial struct VideoSettingsChanged
    /// {
    ///     public readonly int width;
    ///     public readonly int height;
    ///     [DxMessaging.Core.Attributes.DxOptionalParameter] public readonly bool fullscreen; // becomes optional
    /// }
    /// // Generated ctor: VideoSettingsChanged(int width, int height, bool fullscreen = default)
    /// </code>
    /// </example>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct,
        Inherited = false,
        AllowMultiple = false
    )]
    public sealed class DxAutoConstructorAttribute : Attribute { }
}
