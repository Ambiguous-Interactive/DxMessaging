namespace DxMessaging.Core.Attributes
{
    using System;

    /// <summary>
    /// Instructs the source generator to synthesize a constructor matching the fields on the type.
    /// </summary>
    /// <remarks>
    /// Apply this to a partial <c>struct</c> or <c>class</c> to generate a constructor whose parameters
    /// match public instance fields by default, in declaration order within each source file. Across partial files,
    /// parameters follow the compiler's syntax-tree order; use named arguments when file order is not controlled.
    /// Fields marked with
    /// <see cref="DxOptionalParameterAttribute"/> are emitted as optional parameters.
    /// Excluded struct storage receives type defaults while existing field and auto-property initializers
    /// are preserved. For types without a primary constructor, the generated constructor does not
    /// invoke a user parameterless constructor.
    /// When compiling before C# 11, DXMSG011 reports mixed initialized and uninitialized explicit-interface
    /// auto-properties when a user parameterless constructor prevents preserving their initializers.
    /// DXMSG012 reports primary constructors with parameters: remove this attribute and write an explicit
    /// constructor with a this(...) initializer to choose the primary constructor arguments.
    /// An empty primary parameter list is chained automatically with this().
    /// A type with no selected instance fields needs no generated constructor and is not diagnosed.
    /// DXMSG013 reports a record class whose sole selected instance field has its own record type.
    /// Use a property with an object initializer or a different constructor signature to preserve copying.
    /// Enable <see cref="IncludeSerializedFields"/> to include fields marked with <c>UnityEngine.SerializeField</c>
    /// at any accessibility. Enable <see cref="GenerateSerializedProperties"/> to also generate their read-only getters.
    /// Serialized parameter names omit leading underscores; property names uppercase the first remaining character.
    /// DXMSG016 reports invalid names and name collisions. Both options are disabled by default.
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
    public sealed class DxAutoConstructorAttribute : Attribute
    {
        // SYNC: .docs-tests/DocsSnippetCompiler.cs mirrors these named options for snippet compilation.
        /// <summary>
        /// Gets or sets whether fields marked with <c>UnityEngine.SerializeField</c> participate
        /// regardless of accessibility. Their constructor parameter names omit leading underscores.
        /// </summary>
        public bool IncludeSerializedFields { get; set; }

        /// <summary>
        /// Gets or sets whether serialized fields receive public read-only properties.
        /// This also includes those fields in the constructor. Property names omit leading
        /// underscores and uppercase the first remaining character.
        /// </summary>
        public bool GenerateSerializedProperties { get; set; }
    }
}
