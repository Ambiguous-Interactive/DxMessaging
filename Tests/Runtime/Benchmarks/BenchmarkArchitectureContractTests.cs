#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System.Runtime.InteropServices;
    using NUnit.Framework;

    [Category("PerfBench")]
    public sealed class BenchmarkArchitectureContractTests
    {
        [TestCase(Architecture.X86, "x86")]
        [TestCase(Architecture.X64, "x64")]
        [TestCase(Architecture.Arm, "arm")]
        [TestCase(Architecture.Arm64, "arm64")]
        public void ArchitectureLabelsDistinguishInstructionSetsWithTheSamePointerWidth(
            Architecture architecture,
            string expected
        )
        {
            Assert.That(
                DispatchBenchmarkResult.ResolveArchitecture(architecture),
                Is.EqualTo(expected),
                $"architecture={architecture}"
            );
        }

        [Test]
        public void ResultNamesTheActualProcessArchitecture()
        {
            DispatchBenchmarkResult result = DispatchBenchmarkResult.ForEmitScenario(
                "ProcessArchitecture",
                -1,
                1d,
                AllocationProbe.Unmeasured,
                AllocationProbe.Unmeasured,
                1d
            );
            string expected = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            Assert.That(
                result.Platform,
                Does.Contain($" {expected} "),
                "Pointer width cannot distinguish x64 from ARM64, or x86 from ARM."
            );
        }
    }
}
#endif
