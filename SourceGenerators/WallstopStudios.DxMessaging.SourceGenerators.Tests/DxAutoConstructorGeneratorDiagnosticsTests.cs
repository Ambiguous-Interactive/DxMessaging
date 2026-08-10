using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
internal sealed class DxAutoConstructorGeneratorDiagnosticsTests
{
    [Test]
    public void ReportsNonPartialContainingType()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

public class Container
{
    [DxAutoConstructor]
    public readonly struct NestedMessage
    {
        public readonly int value;
    }
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG003"),
            "DXMSG003 should be reported when nested types are not declared inside partial containers."
        );
        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG004"),
            "DXMSG004 should suggest adding the partial keyword for the containing type."
        );
    }

    [Test]
    public void ReportsInvalidOptionalDefaultExpression()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxTargetedMessage]
[DxAutoConstructor]
public readonly partial struct InvalidOptional
{
    [DxOptionalParameter(Expression = "value > 0")]
    public readonly int value;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG005"),
            "DXMSG005 should be reported for optional defaults that cannot be parsed."
        );
    }

    // ------------------------------------------------------------------------------------------
    // Phase D: [DxOptionalParameter] permutations on primitive types (constructor-constant path).
    // ------------------------------------------------------------------------------------------

    [Test]
    public void OptionalParameterIntDefaultEmitsConstructorWithLiteral()
    {
        AssertOptionalProducesDefault(
            fieldType: "int",
            attributeArg: "42",
            expectedDefaultLiteral: "42"
        );
    }

    [Test]
    public void OptionalParameterLongDefaultEmitsConstructorWithLiteral()
    {
        // Long literals must be suffixed with 'L' to disambiguate from int.
        AssertOptionalProducesDefault(
            fieldType: "long",
            attributeArg: "9000000000L",
            expectedDefaultLiteral: "9000000000L"
        );
    }

    [Test]
    public void OptionalParameterFloatDefaultEmitsConstructorWithLiteral()
    {
        AssertOptionalProducesDefault(
            fieldType: "float",
            attributeArg: "3.14f",
            expectedDefaultLiteral: "3.14f"
        );
    }

    [Test]
    public void OptionalParameterDoubleDefaultEmitsConstructorWithLiteral()
    {
        AssertOptionalProducesDefault(
            fieldType: "double",
            attributeArg: "2.718",
            expectedDefaultLiteral: "2.718"
        );
    }

    [Test]
    public void OptionalParameterBoolDefaultEmitsConstructorWithLiteral()
    {
        AssertOptionalProducesDefault(
            fieldType: "bool",
            attributeArg: "true",
            expectedDefaultLiteral: "true"
        );
    }

    [Test]
    public void OptionalParameterStringDefaultEmitsConstructorWithLiteral()
    {
        AssertOptionalProducesDefault(
            fieldType: "string",
            attributeArg: "\"hello\"",
            expectedDefaultLiteral: "\"hello\""
        );
    }

    [Test]
    public void OptionalParameterCharDefaultEmitsConstructorWithLiteral()
    {
        AssertOptionalProducesDefault(
            fieldType: "char",
            attributeArg: "'x'",
            expectedDefaultLiteral: "'x'"
        );
    }

    [Test]
    public void OptionalParameterByteDefaultEmitsConstructorWithLiteral()
    {
        AssertOptionalProducesDefault(
            fieldType: "byte",
            attributeArg: "(byte)7",
            expectedDefaultLiteral: "7"
        );
    }

    [Test]
    public void OptionalParameterShortDefaultEmitsConstructorWithLiteral()
    {
        AssertOptionalProducesDefault(
            fieldType: "short",
            attributeArg: "(short)11",
            expectedDefaultLiteral: "11"
        );
    }

    // ------------------------------------------------------------------------------------------
    // Phase D: [DxOptionalParameter(Expression = "...")] permutations.
    // ------------------------------------------------------------------------------------------

    [Test]
    public void OptionalParameterStringNullableDefaultViaExpressionEmits()
    {
        // string? with `Expression = "null"` should bind cleanly because the field is a reference
        // type, satisfying IsReferenceOrNullable.
        string source = """
#nullable enable
using DxMessaging.Core.Attributes;

namespace Sample;

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter(Expression = "null")]
    public readonly string? maybe;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty,
            "string? with Expression=\"null\" must not emit DXMSG005."
        );
        AssertGeneratedSourceContains(
            result,
            "= null",
            "Generated constructor should default the parameter to `null`."
        );
    }

    [Test]
    public void OptionalParameterEnumLiteralViaExpressionEmits()
    {
        // Enum literal via Expression; the named-argument path runs IsValidDefaultExpression,
        // which speculatively binds the expression at the type's syntax position. The enum is
        // declared in the same compilation, so the binding should succeed.
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

public enum MyEnum
{
    Default,
    Other,
}

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter(Expression = "MyEnum.Other")]
    public readonly MyEnum value;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty,
            "Enum literal via Expression must not emit DXMSG005."
        );
        AssertGeneratedSourceContains(
            result,
            "= MyEnum.Other",
            "Generated constructor should default to MyEnum.Other."
        );
    }

    [Test]
    public void OptionalParameterDefaultStructViaExpressionEmits()
    {
        // `default(MyStruct)` is currently the canonical way to express a struct's default value
        // through DxOptionalParameter. The bare `default` literal is also accepted (the generator
        // short-circuits on the literal `default` in IsValidDefaultExpression).
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

public struct MyStruct
{
    public int value;
}

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter(Expression = "default(MyStruct)")]
    public readonly MyStruct s;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty,
            "default(MyStruct) via Expression must not emit DXMSG005."
        );
        AssertGeneratedSourceContains(
            result,
            "= default(MyStruct)",
            "Generated constructor should default the parameter to `default(MyStruct)`."
        );
    }

    [Test]
    public void OptionalParameterBareDefaultLiteralEmits()
    {
        // Special case: the generator short-circuits on the literal token `default`; it is valid
        // for any type because the parameter slot supplies the type context.
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter(Expression = "default")]
    public readonly int value;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty,
            "Bare `default` Expression must not emit DXMSG005."
        );
        AssertGeneratedSourceContains(
            result,
            "= default",
            "Generated constructor should default the parameter to `default`."
        );
    }

    // ------------------------------------------------------------------------------------------
    // Phase D: DXMSG005 emission boundary cases.
    // ------------------------------------------------------------------------------------------

    [Test]
    public void DXMSG005DoesNotFireForRuntimeExpressionWhoseTypeMatches()
    {
        // PINS CURRENT CONTRACT (slightly weaker than the brief assumed): the generator's
        // IsValidDefaultExpression speculatively binds the expression and checks only that the
        // bound type is implicitly convertible to the field type. It does NOT verify that the
        // expression is a compile-time constant. Therefore a non-constant expression like
        // `System.DateTime.Now.Ticks` (whose type is `long`, convertible to a `long` field) passes
        // DXMSG005's check. The C# compiler then surfaces the real problem as CS1736 on the
        // generated constructor signature. This test pins THAT behavior so a future tightening of
        // IsValidDefaultExpression to require constants is a deliberate, visible change.
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter(Expression = "System.DateTime.Now.Ticks")]
    public readonly long value;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics.Where(d => d.Id == "DXMSG005"),
            Is.Empty,
            "Pin: DXMSG005 currently does not fire for non-constant expressions whose bound type "
                + "matches the field type. Constant-ness is enforced downstream by CS1736."
        );
    }

    [Test]
    public void DXMSG005FiresForUnparseableExpression()
    {
        // Pin: a syntactically invalid expression must surface as DXMSG005 (the generator catches
        // the parse exception and reports the diagnostic rather than crashing).
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter(Expression = "(((")]
    public readonly int value;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG005"),
            "DXMSG005 should fire for unparseable Expression strings."
        );
    }

    [Test]
    public void DXMSG005FiresForTypeMismatchedExpression()
    {
        // Pin: `Expression = "\"hello\""` on an `int` field must be rejected; there is no
        // implicit conversion from string -> int.
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter(Expression = "\"hello\"")]
    public readonly int value;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG005"),
            "DXMSG005 should fire when the Expression's type cannot implicitly convert to the field type."
        );
    }

    [Test]
    public void DXMSG005FiresForNullOnValueTypeField()
    {
        // Pin: `[DxOptionalParameter(null)]` on an `int` field is invalid because int is neither
        // a reference type nor a Nullable<T>.
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter(null)]
    public readonly int value;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG005"),
            "DXMSG005 should fire when null is passed as the default for a non-nullable value type."
        );
    }

    [Test]
    public void DXMSG005DoesNotFireForCompatiblePrimitiveConstant()
    {
        // Boundary: a literal `int` constant on an `int` field must NOT trip DXMSG005.
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter(7)]
    public readonly int value;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics.Where(d => d.Id == "DXMSG005"),
            Is.Empty,
            "DXMSG005 should not fire for compatible primitive constants."
        );
        AssertGeneratedSourceContains(
            result,
            "= 7",
            "Generated constructor should default the parameter to `7`."
        );
    }

    // ------------------------------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Drives the auto-constructor generator with a single optional field of the given type and
    /// default attribute argument, then asserts no DXMSG005 fires AND the expected literal lands
    /// in the generated constructor signature.
    /// </summary>
    private static void AssertOptionalProducesDefault(
        string fieldType,
        string attributeArg,
        string expectedDefaultLiteral
    )
    {
        string source = $$"""
using DxMessaging.Core.Attributes;

namespace Sample;

[DxAutoConstructor]
public readonly partial struct M
{
    [DxOptionalParameter({{attributeArg}})]
    public readonly {{fieldType}} value;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxAutoConstructor(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics.Where(d => d.Id == "DXMSG005"),
            Is.Empty,
            $"DXMSG005 must not fire for {fieldType} with attribute arg {attributeArg}."
        );
        AssertGeneratedSourceContains(
            result,
            $"= {expectedDefaultLiteral}",
            $"Generated constructor should default the {fieldType} parameter to {expectedDefaultLiteral}."
        );
    }

    private static void AssertGeneratedSourceContains(
        GeneratorDriverRunResult result,
        string fragment,
        string failureMessage
    )
    {
        string joined = string.Join(
            "\n",
            result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );
        Assert.That(joined, Does.Contain(fragment), failureMessage);
    }
}
