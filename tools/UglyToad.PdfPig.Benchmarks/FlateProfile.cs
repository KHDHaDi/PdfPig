using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Benchmarks;

/// <summary>
/// Where the Flate filter's time goes with the managed inflater, by the size of the stream: how
/// much of it is building the tables of dynamic blocks, how much decoding, and how much the
/// plumbing around the inflater. Small content streams and large image streams have different
/// bottlenecks, and an idea that helps one may cost the other, so each size class is shown on
/// its own. The inflater's own counters are switched on through reflection.
/// </summary>
/// <remarks>
/// Run with <c>dotnet run -c Release -f net8.0 -- flate-profile [folder [max-files [stride]]]</c>.
/// Without a folder the documents the benchmarks ship with are profiled; with one, every
/// stride-th PDF under it, up to max-files, so that a sample spreads over a corpus.
/// </remarks>
internal static class FlateProfile
{
    private static readonly (string Name, int Limit)[] Buckets =
    [
        ("<= 1 KB", 1024),
        ("1-4 KB", 4 * 1024),
        ("4-32 KB", 32 * 1024),
        ("32-256 KB", 256 * 1024),
        ("> 256 KB", int.MaxValue)
    ];

    public static void Run(string[] args)
    {
        var streams = args.Length > 1
            ? FlateStreams.Load(FlateStreams.Files(args[1], args.Length > 2 ? int.Parse(args[2]) : int.MaxValue, args.Length > 3 ? int.Parse(args[3]) : 1), includePredictors: false)
            : FlateStreams.Load();

        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}, {streams.Length} plain Flate streams");
        Console.WriteLine();

        var filter = new FlateFilter();
        SetManaged(true);

        var grouped = Buckets.Select(bucket => streams.Where(s => InBucket(s.Data.Length, bucket)).ToArray()).ToArray();

        var results = new List<Row>();

        for (var i = 0; i < Buckets.Length; i++)
        {
            if (grouped[i].Length == 0)
            {
                continue;
            }

            results.Add(Measure(Buckets[i].Name, grouped[i], filter));
        }

        results.Add(Measure("all", streams, filter));

        Console.WriteLine($"{"size",-10} {"streams",8} {"in KB",9} {"out KB",9} {"blocks dyn/fix/sto",20} {"ms/pass",9} {"MB/s out",9} {"tables",7} {"decode",7} {"other",7} {"grows",6} {"grow",6} {"copied",7} {"copy",6}");

        foreach (var row in results)
        {
            Console.WriteLine($"{row.Name,-10} {row.Streams,8} {row.InBytes / 1024,9} {row.OutBytes / 1024,9} {row.Blocks,20} {row.Milliseconds,9:F3} {row.OutBytes / row.Milliseconds / 1000,9:F0} {row.TableShare,6:P0} {row.DecodeShare,6:P0} {row.OtherShare,6:P0} {row.Grows,6} {row.GrowShare,5:P0} {row.Copied,7} {row.CopyShare,5:P0}");
        }

        Console.WriteLine();
        Console.WriteLine("ms/pass is the least over all passes of the filter's time over the streams of the size class; the shares are averages of the inflater's own counters.");
        Console.WriteLine("grows: output buffers that had to grow, with the share of their copies (within decode); copied: results copied into an exact array rather than kept in the buffer, with the share of those copies (within other).");
    }

    private static bool InBucket(int length, (string Name, int Limit) bucket)
    {
        var index = Array.IndexOf(Buckets, bucket);
        var lower = index == 0 ? 0 : Buckets[index - 1].Limit;

        return length > lower && length <= bucket.Limit;
    }

    private static Row Measure(string name, (byte[] Data, DictionaryToken Dictionary)[] streams, FlateFilter filter)
    {
        long outBytes = 0;

        // Warm up, and learn how long a pass takes, so that the passes add up to a few seconds.
        var warm = Stopwatch.GetTimestamp();

        for (var i = 0; i < 2; i++)
        {
            outBytes = Pass(streams, filter);
        }

        var perPass = (Stopwatch.GetTimestamp() - warm) / 2.0;
        var passes = (int)Math.Clamp(3 * Stopwatch.Frequency / Math.Max(perPass, 1), 5, 200);

        InflaterProfile.Reset();
        InflaterProfile.Enable(true);

        var best = long.MaxValue;
        long total = 0;

        for (var i = 0; i < passes; i++)
        {
            var started = Stopwatch.GetTimestamp();
            Pass(streams, filter);
            var elapsed = Stopwatch.GetTimestamp() - started;
            best = Math.Min(best, elapsed);
            total += elapsed;
        }

        InflaterProfile.Enable(false);
        var counters = InflaterProfile.Read();

        // The inflater's counters were accumulated over all passes, so the shares are taken
        // against the time of all passes; the time shown is the best pass.
        var tableShare = counters.TableTicks / (double)total;
        var decodeShare = (counters.DecodeTicks + counters.StoredTicks) / (double)total;

        return new Row(
            name,
            streams.Length,
            streams.Sum(s => (long)s.Data.Length),
            outBytes,
            $"{counters.DynamicBlocks / passes}/{counters.FixedBlocks / passes}/{counters.StoredBlocks / passes}",
            best * 1000.0 / Stopwatch.Frequency,
            tableShare,
            decodeShare,
            Math.Max(0, 1 - tableShare - decodeShare),
            counters.Grows / passes,
            counters.GrowTicks / (double)total,
            counters.ResultsCopied / passes,
            counters.ResultCopyTicks / (double)total);
    }

    private static long Pass((byte[] Data, DictionaryToken Dictionary)[] streams, FlateFilter filter)
    {
        long total = 0;

        foreach (var (data, dictionary) in streams)
        {
            total += filter.Decode(data, dictionary, DefaultFilterProvider.Instance, 0).Length;
        }

        return total;
    }

    private static void SetManaged(bool managed)
    {
        typeof(FlateFilter).GetField("UseManagedInflater", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, managed);
    }

    private sealed record Row(string Name, int Streams, long InBytes, long OutBytes, string Blocks, double Milliseconds, double TableShare, double DecodeShare, double OtherShare, long Grows, double GrowShare, long Copied, double CopyShare);

    /// <summary>The inflater's profile counters, reached through reflection since the assembly is signed.</summary>
    private static class InflaterProfile
    {
        private static readonly Type Type = typeof(FlateFilter).Assembly.GetType("UglyToad.PdfPig.Filters.Flate.Inflater+Profile")
            ?? throw new InvalidOperationException("The inflater has no profile counters in this build.");

        public static void Enable(bool enabled) => Field("Enabled").SetValue(null, enabled);

        public static void Reset() => Type.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);

        public static (long DynamicBlocks, long FixedBlocks, long StoredBlocks, long TableTicks, long DecodeTicks, long StoredTicks, long Grows, long GrowTicks, long ResultsCopied, long ResultCopyTicks) Read() =>
            (Long("DynamicBlocks"), Long("FixedBlocks"), Long("StoredBlocks"), Long("TableTicks"), Long("DecodeTicks"), Long("StoredTicks"), Long("Grows"), Long("GrowTicks"), Long("ResultsCopied"), Long("ResultCopyTicks"));

        private static long Long(string name) => (long)Field(name).GetValue(null)!;

        private static FieldInfo Field(string name) => Type.GetField(name, BindingFlags.Public | BindingFlags.Static)!;
    }
}
