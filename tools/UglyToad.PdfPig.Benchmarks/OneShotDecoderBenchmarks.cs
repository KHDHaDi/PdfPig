#if INCLUDE_NET11
using System.Buffers;
using System.IO.Compression;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Benchmarks;

/// <summary>
/// .NET 11 adds one-shot decoders over spans, ZLibDecoder and DeflateDecoder, that skip the
/// stream machinery DeflateStream pays for on every read. Here they inflate the same streams as
/// the filter does, with the filter's buffering around them, against the filter with either of
/// its inflaters. Opted into with the .NET 11 preview SDK installed; the class then compiles for
/// every framework, so that a .NET 8 host discovers it, but its one job runs on .NET 11, where
/// alone the decoders exist:
/// <c>dotnet run -f net8.0 -c Release -p:IncludeNet11=true -- --filter *OneShotDecoderBenchmarks*</c>.
/// </summary>
[Config(typeof(Net11Config))]
[MemoryDiagnoser(displayGenColumns: false)]
public class OneShotDecoderBenchmarks
{
    private readonly FlateFilter filter = new();

    private (byte[] Data, DictionaryToken Dictionary)[] streams = [];

    [GlobalSetup]
    public void Setup()
    {
        streams = FlateStreams.Load();
    }

    /// <summary>The filter with DeflateStream, native zlib-ng.</summary>
    [Benchmark(Baseline = true)]
    public long Filter()
    {
        UseManagedInflater(false);

        return RunFilter();
    }

    /// <summary>The filter with the managed inflater.</summary>
    [Benchmark]
    public long FilterManaged()
    {
        UseManagedInflater(true);

        return RunFilter();
    }

    /// <summary>ZLibDecoder over the whole frame: header and Adler-32 checked.</summary>
    [Benchmark]
    public long ZLibDecoderOneShot()
    {
        long total = 0;

        foreach (var (data, _) in streams)
        {
            total += Inflate(data, 0, raw: false);
        }

        return total;
    }

    /// <summary>DeflateDecoder over the raw deflate data, as the filter's inflaters read it.</summary>
    [Benchmark]
    public long DeflateDecoderOneShot()
    {
        long total = 0;

        foreach (var (data, _) in streams)
        {
            total += Inflate(data, 2, raw: true);
        }

        return total;
    }

    /// <summary>
    /// The same with a buffer of sixteen times the input from the start: a one-shot decoder that
    /// runs out of room starts over, so the opening guess decides its speed.
    /// </summary>
    [Benchmark]
    public long DeflateDecoderOneShot16()
    {
        long total = 0;

        foreach (var (data, _) in streams)
        {
            total += Inflate(data, 2, raw: true, factor: 16);
        }

        return total;
    }

    private long RunFilter()
    {
        long total = 0;

        foreach (var (data, dictionary) in streams)
        {
            total += filter.Decode(data, dictionary, DefaultFilterProvider.Instance, 0).Length;
        }

        return total;
    }

    private static void UseManagedInflater(bool managed)
    {
        typeof(FlateFilter).GetField("UseManagedInflater", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, managed);
    }

    /// <summary>
    /// What the filter does around an inflater: a rented buffer of four times the input, doubled
    /// when it turns out too small, and an exact copy as the result. A one-shot decoder cannot
    /// carry on into a larger buffer, so doubling starts it over.
    /// </summary>
    private static int Inflate(byte[] frame, int skip, bool raw, int factor = 4)
    {
#if NET11_0_OR_GREATER
        var input = frame.AsSpan(skip);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, input.Length * factor));

        try
        {
            while (true)
            {
                // A fresh decoder per attempt: in preview 7 an instance decompresses on its first
                // call only (dotnet/runtime#129090).
                OperationStatus status;
                int written;

                if (raw)
                {
                    using var decoder = new DeflateDecoder();
                    status = decoder.Decompress(input, buffer, out _, out written);
                }
                else
                {
                    using var decoder = new ZLibDecoder();
                    status = decoder.Decompress(input, buffer, out _, out written);
                }

                if (status == OperationStatus.DestinationTooSmall)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                    continue;
                }

                if (status == OperationStatus.InvalidData)
                {
                    return 0;
                }

                return buffer.AsSpan(0, written).ToArray().Length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
#else
        throw new PlatformNotSupportedException("The one-shot decoders exist from .NET 11 on; this method runs in the net11.0 job only.");
#endif
    }
}

/// <summary>
/// The local build on .NET 11 alone, for APIs that exist there only. BenchmarkDotNet 0.15.8 does
/// not know .NET 11, so the job is built through an explicit toolchain, and the Runtime column
/// shows the host's runtime for it; the Job column tells.
/// </summary>
internal class Net11Config : ManualConfig
{
    public Net11Config()
    {
        AddJob(Job.Default.WithMsBuildArguments("/p:PdfPigVersion=Local /p:IncludeNet11=true").WithToolchain(CsProjCoreToolchain.From(new NetCoreAppSettings("net11.0", null, ".NET 11.0"))).WithId("net11.0"));
    }
}
#endif
