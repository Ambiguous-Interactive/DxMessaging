#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System.Runtime.InteropServices;
    using NUnit.Framework;

    [Category("PerfBench")]
    public sealed class BenchmarkArchitectureContractTests
    {
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
