using System.Collections;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

/// <remarks>
/// 2026-10-27: valid signatures overflowed when consumer compilation enabled integer
/// overflow checks. Compile the actual shipped key under both options and exercise
/// construction, equality, hash stability, dictionary lookup, and default/null contracts.
/// 2026-10-28: registration handles had the same implicit-wraparound defect; compile
/// the whole portable Runtime and preserve reference identity for custom handle state.
/// </remarks>
[TestFixture]
internal sealed class RuntimeHashCompilationTests
{
    private static readonly MetadataReference[] References = (
        (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("Runtime reference assemblies are unavailable.")
    )
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    private static readonly Lazy<Assembly> UncheckedRuntime = new(() =>
        CompilePortableRuntime(false)
    );
    private static readonly Lazy<Assembly> CheckedRuntime = new(() => CompilePortableRuntime(true));

    private static IEnumerable<TestCaseData> RegistrationCases()
    {
        foreach (bool checkOverflow in new[] { false, true })
        {
            foreach (long id in new[] { 0L, 1L, long.MinValue, long.MaxValue })
            {
                foreach (bool hasState in new[] { false, true })
                {
                    yield return new TestCaseData(checkOverflow, id, hasState);
                }
            }
        }
    }

    [TestCaseSource(nameof(RegistrationCases))]
    public void ExternalRegistrationsRemainDictionaryKeys(
        bool checkOverflow,
        long id,
        bool hasState
    )
    {
        Type keyType = GetPortableRuntime(checkOverflow)
            .GetType("DxMessaging.Core.MessageBus.MessageBusRegistration", true)!;
        ConstructorInfo constructor = keyType.GetConstructor(
            new[] { typeof(long), typeof(object) }
        )!;
        IdentityOnlyState? state = hasState ? new IdentityOnlyState() : null;
        object key = constructor.Invoke(new object?[] { id, state });
        object equalKey = constructor.Invoke(new object?[] { id, state });
        IdentityOnlyState otherState = new();
        object otherKey = constructor.Invoke(new object?[] { id, otherState });
        string label = $"checked={checkOverflow}; id={id}; state={hasState}";
        Assert.That(key, Is.EqualTo(equalKey), label);
        Assert.That(key, Is.Not.EqualTo(otherKey), label);
        int firstHash = key.GetHashCode();
        Assert.That(firstHash, Is.EqualTo(equalKey.GetHashCode()), label);
        Assert.That(key.GetHashCode(), Is.EqualTo(firstHash), label);
        AssertDictionaryRoundTrip(keyType, key, equalKey, label);
        Assert.That(state?.HashCalls ?? 0, Is.Zero, label);
        Assert.That(state?.EqualityCalls ?? 0, Is.Zero, label);
        Assert.That(otherState.HashCalls, Is.Zero, label);
        Assert.That(otherState.EqualityCalls, Is.Zero, label);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DefaultRegistrationRemainsADictionaryKey(bool checkOverflow)
    {
        Type keyType = GetPortableRuntime(checkOverflow)
            .GetType("DxMessaging.Core.MessageBus.MessageBusRegistration", true)!;
        object key = Activator.CreateInstance(keyType)!;
        object equalKey = Activator.CreateInstance(keyType)!;
        string label = $"checked={checkOverflow}";
        Assert.That(key, Is.EqualTo(equalKey), label);
        Assert.That(key.GetHashCode(), Is.EqualTo(equalKey.GetHashCode()), label);
        AssertDictionaryRoundTrip(keyType, key, equalKey, label);
    }

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
        Type keyType = GetPortableRuntime(checkOverflow)
            .GetType("DxMessaging.Core.Messages.MethodSignatureKey", true)!;
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

        AssertDictionaryRoundTrip(
            keyType,
            key,
            equalKey,
            $"checked={checkOverflow}; shape={shape}"
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DefaultAndNullNameContractsSurviveCompilation(bool checkOverflow)
    {
        Type keyType = GetPortableRuntime(checkOverflow)
            .GetType("DxMessaging.Core.Messages.MethodSignatureKey", true)!;
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

    private static void AssertDictionaryRoundTrip(
        Type keyType,
        object key,
        object equalKey,
        string label
    )
    {
        IDictionary dictionary = (IDictionary)
            Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, typeof(int)))!;
        dictionary.Add(key, 42);
        Assert.That(dictionary[equalKey], Is.EqualTo(42), label);
        dictionary.Remove(equalKey);
        Assert.That(dictionary.Count, Is.Zero, label);
    }

    private static Assembly GetPortableRuntime(bool checkOverflow) =>
        (checkOverflow ? CheckedRuntime : UncheckedRuntime).Value;

    private static Assembly CompilePortableRuntime(bool checkOverflow)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }
        Assert.That(directory, Is.Not.Null, "Repository root must be found.");
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp9);
        /* Match the maintained portable project's recursive Runtime C# inputs, without Unity symbols. */
        SyntaxTree[] trees = Directory
            .EnumerateFiles(
                Path.Combine(directory!.FullName, "Runtime"),
                "*.cs",
                SearchOption.AllDirectories
            )
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), parseOptions, path))
            .ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(
            "PortableRuntime_" + Guid.NewGuid().ToString("N"),
            trees,
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                checkOverflow: checkOverflow,
                allowUnsafe: true
            )
        );
        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
        Assert.That(result.Success, Is.True, string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }

    private sealed class IdentityOnlyState
    {
        public int HashCalls { get; private set; }
        public int EqualityCalls { get; private set; }

        public override int GetHashCode()
        {
            HashCalls++;
            return 17;
        }

        public override bool Equals(object? other)
        {
            EqualityCalls++;
            return other is IdentityOnlyState;
        }
    }

    private sealed class MaximumHashType : TypeDelegator
    {
        public MaximumHashType()
            : base(typeof(int)) { }

        public override int GetHashCode() => int.MaxValue;
    }
}
