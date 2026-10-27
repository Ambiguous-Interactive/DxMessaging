using System.Collections;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

/// <remarks>
/// 2026-10-27: valid signatures overflowed when consumer compilation enabled integer
/// overflow checks. Compile the actual shipped key under both options and exercise
/// construction, equality, hash stability, dictionary lookup, and default/null contracts.
/// </remarks>
[TestFixture]
internal sealed class MethodSignatureKeyCompilationTests
{
    private static readonly MetadataReference[] References = (
        (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("Runtime reference assemblies are unavailable.")
    )
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    [TestCase(false, "null")]
    [TestCase(true, "null")]
    [TestCase(false, "empty")]
    [TestCase(true, "empty")]
    [TestCase(false, "runtime")]
    [TestCase(true, "runtime")]
    [TestCase(false, "maximum-hash")]
    [TestCase(true, "maximum-hash")]
    public void ValidSignaturesRemainDictionaryKeys(bool checkOverflow, string shape)
    {
        Type keyType = CompileShippedKey(checkOverflow);
        ConstructorInfo constructor = keyType.GetConstructor(
            new[] { typeof(string), typeof(Type[]) }
        )!;
        Type[]? parameterTypes = shape switch
        {
            "null" => null,
            "empty" => Type.EmptyTypes,
            "runtime" => new[] { typeof(int), typeof(string), typeof(object) },
            "maximum-hash" => new Type[] { new MaximumHashType(), new MaximumHashType() },
            _ => throw new ArgumentOutOfRangeException(
                nameof(shape),
                shape,
                "Unknown signature shape."
            ),
        };
        object key = constructor.Invoke(new object?[] { "Receive", parameterTypes });
        object equalKey = constructor.Invoke(new object?[] { "Receive", parameterTypes });
        Assert.That(key, Is.EqualTo(equalKey), $"checked={checkOverflow}; shape={shape}");
        Assert.That(
            key.GetHashCode(),
            Is.EqualTo(equalKey.GetHashCode()),
            $"checked={checkOverflow}; shape={shape}"
        );

        IDictionary dictionary = (IDictionary)
            Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, typeof(int)))!;
        dictionary.Add(key, 42);
        Assert.That(
            dictionary[equalKey],
            Is.EqualTo(42),
            $"checked={checkOverflow}; shape={shape}"
        );
        dictionary.Remove(equalKey);
        Assert.That(dictionary.Count, Is.Zero, $"checked={checkOverflow}; shape={shape}");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DefaultAndNullNameContractsSurviveCompilation(bool checkOverflow)
    {
        Type keyType = CompileShippedKey(checkOverflow);
        ConstructorInfo constructor = keyType.GetConstructor(
            new[] { typeof(string), typeof(Type[]) }
        )!;
        object defaultKey = Activator.CreateInstance(keyType)!;
        Assert.That(
            defaultKey,
            Is.EqualTo(Activator.CreateInstance(keyType)),
            $"checked={checkOverflow}"
        );
        Assert.That(defaultKey.GetHashCode(), Is.Zero, $"checked={checkOverflow}");
        object constructedKey = constructor.Invoke(new object?[] { "", Type.EmptyTypes });
        Assert.That(defaultKey, Is.Not.EqualTo(constructedKey), $"checked={checkOverflow}");
        TargetInvocationException? exception = Assert.Throws<TargetInvocationException>(() =>
            constructor.Invoke(new object?[] { null, Type.EmptyTypes })
        );
        Assert.That(
            exception!.InnerException,
            Is.TypeOf<ArgumentNullException>(),
            $"checked={checkOverflow}"
        );
        Assert.That(
            ((ArgumentNullException)exception.InnerException!).ParamName,
            Is.EqualTo("methodName"),
            $"checked={checkOverflow}"
        );
    }

    private static Type CompileShippedKey(bool checkOverflow)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }
        Assert.That(directory, Is.Not.Null, "Repository root must be found.");
        string sourcePath = Path.Combine(
            directory!.FullName,
            "Runtime",
            "Core",
            "Messages",
            "ReflexiveMessage.cs"
        );
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp9);
        SyntaxTree source = CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath), parseOptions);
        StructDeclarationSyntax declaration = source
            .GetRoot()
            .DescendantNodes()
            .OfType<StructDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MethodSignatureKey");
        /*
         * Compile the shipped declaration without rewriting its members. This isolates the
         * public key's compiler-option contract from unrelated Unity message interfaces.
         */
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "using System; namespace DxMessaging.Core.Messages { "
                + declaration.ToFullString()
                + " }",
            parseOptions,
            sourcePath
        );
        CSharpCompilation compilation = CSharpCompilation.Create(
            "SignatureKey_" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                checkOverflow: checkOverflow
            )
        );
        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
        Assert.That(result.Success, Is.True, string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly
            .Load(stream.ToArray())
            .GetType("DxMessaging.Core.Messages.MethodSignatureKey", true)!;
    }

    private sealed class MaximumHashType : TypeDelegator
    {
        public MaximumHashType()
            : base(typeof(int)) { }

        public override int GetHashCode() => int.MaxValue;
    }
}
