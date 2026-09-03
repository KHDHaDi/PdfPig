using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;

namespace UglyToad.PdfPig.Benchmarks;

/// <summary>
/// The local build on .NET 8, .NET 9 and .NET 11, for code whose speed depends on the runtime
/// rather than on PdfPig: DeflateStream inflates through native zlib on .NET 8 and through
/// zlib-ng from .NET 9 on. .NET 11 needs its preview SDK, so its job is opted into with
/// <c>-p:IncludeNet11=true</c>; BenchmarkDotNet 0.15.8 does not know that runtime, so the job is
/// built through an explicit toolchain and the Runtime column shows the host's runtime for it.
/// The Job column tells.
/// </summary>
internal class RuntimesConfig : ManualConfig
{
    public RuntimesConfig()
    {
        var local = Job.Default.WithMsBuildArguments("/p:PdfPigVersion=Local");

        AddJob(local.WithRuntime(CoreRuntime.Core80).WithId("net8.0").AsBaseline());
        AddJob(local.WithRuntime(CoreRuntime.Core90).WithId("net9.0"));
#if INCLUDE_NET11
        AddJob(local.WithMsBuildArguments("/p:PdfPigVersion=Local /p:IncludeNet11=true").WithToolchain(CsProjCoreToolchain.From(new NetCoreAppSettings("net11.0", null, ".NET 11.0"))).WithId("net11.0"));
#endif
    }
}
