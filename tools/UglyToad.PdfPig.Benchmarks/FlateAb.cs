using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UglyToad.PdfPig.Filters.Flate;

namespace UglyToad.PdfPig.Benchmarks;

/// <summary>
/// The inflater at hand against a snapshot of an earlier one, both compiled into this assembly and
/// called directly on the raw deflate data of the same streams, in interleaved rounds, so that the
/// two see the same machine and a drift of it over the minutes falls on both alike. Measuring the
/// two in separate runs was not good enough: the runs differed by more than the changes did.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot is InflaterBaseline.cs beside this file, which is not in the repository: make it
/// from the commit to compare against with
/// <c>git show commit:src/UglyToad.PdfPig/Filters/Flate/Inflater.cs | sed 's/class Inflater$/class InflaterBaseline/' &gt; InflaterBaseline.cs</c>
/// and build with <c>-p:FlateAb=true</c>, which compiles the inflater's source into this assembly
/// as well.
/// </para>
/// <para>
/// Run with <c>dotnet run -c Release -f net8.0 -p:FlateAb=true -- flate-ab [--filter] [folder [max-files [stride]]]</c>.
/// Without <c>--filter</c> each stream is inflated into a buffer that already fits it, so that the
/// decoding alone is compared; with it, the filter's plumbing is done around the inflater as the
/// filter does it, a buffer rented at four times the input, kept as the result when it is three
/// quarters full and copied into an exact array otherwise, so that the buffer handling is compared.
/// </para>
/// </remarks>
internal static class FlateAb
{
    private const int MaximumCapacity = 0x7FFFFFC7;

    /// <summary>The rows beside the baseline: the current inflater, set up for each by the action, when there is a knob to turn.</summary>
    private static readonly (string Name, Action Setup)[] Variants =
    [
        ("current", () => { }),
    ];

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
        var positional = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();
        var plumbing = args.Contains("--filter");

        var loaded = positional.Length > 0
            ? FlateStreams.Load(FlateStreams.Files(positional[0], positional.Length > 1 ? int.Parse(positional[1]) : int.MaxValue, positional.Length > 2 ? int.Parse(positional[2]) : 1), includePredictors: false)
            : FlateStreams.Load();

        // The raw deflate data behind the zlib header, and a buffer for each that fits its output,
        // so that in the inflater-only mode neither inflater grows or copies anything.
        var streams = loaded.Select(s => s.Data.AsMemory(2)).ToArray();
        var buffers = streams.Select(s =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, s.Length * 4));
            InflaterBaseline.Inflate(s.Span, ref buffer, out _);
            return buffer;
        }).ToArray();

        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}, {streams.Length} raw deflate streams, {(plumbing ? "with the filter's buffer handling" : "inflater only")}");
        Console.WriteLine();

        var groups = Buckets.Select((bucket, i) => Enumerable.Range(0, streams.Length).Where(s => InBucket(streams[s].Length, i)).ToArray()).Append(Enumerable.Range(0, streams.Length).ToArray()).ToArray();
        var names = Buckets.Select(b => b.Name).Append("all").ToArray();

        var rows = new List<(string Name, Action<int[]> Pass)>
        {
            ("baseline (HEAD)", group => { if (plumbing) BaselineWithPlumbing(group, streams); else RunBaseline(group, streams, buffers); })
        };

        foreach (var variant in Variants)
        {
            rows.Add((variant.Name, group =>
            {
                variant.Setup();

                if (plumbing)
                {
                    CurrentWithPlumbing(group, streams);
                }
                else
                {
                    RunCurrent(group, streams, buffers);
                }
            }));
        }

        var best = new long[rows.Count, groups.Length];

        for (var r = 0; r < rows.Count; r++)
        {
            for (var g = 0; g < groups.Length; g++)
            {
                best[r, g] = long.MaxValue;
            }
        }

        for (var g = 0; g < groups.Length; g++)
        {
            if (groups[g].Length == 0)
            {
                continue;
            }

            // Warm every row up once, then rounds of one pass per row.
            foreach (var row in rows)
            {
                row.Pass(groups[g]);
            }

            var started = Stopwatch.GetTimestamp();
            rows[0].Pass(groups[g]);
            var perPass = Stopwatch.GetTimestamp() - started;
            var rounds = (int)Math.Clamp(5 * Stopwatch.Frequency / Math.Max(perPass * rows.Count, 1), 7, 200);

            for (var round = 0; round < rounds; round++)
            {
                for (var r = 0; r < rows.Count; r++)
                {
                    started = Stopwatch.GetTimestamp();
                    rows[r].Pass(groups[g]);
                    best[r, g] = Math.Min(best[r, g], Stopwatch.GetTimestamp() - started);
                }
            }
        }

        Console.WriteLine($"{"inflater",-42} " + string.Join(" ", names.Select(n => $"{n,10}")));

        for (var r = 0; r < rows.Count; r++)
        {
            var cells = new List<string>();

            for (var g = 0; g < groups.Length; g++)
            {
                cells.Add(groups[g].Length == 0 ? $"{"-",10}" : $"{best[r, g] * 1000.0 / Stopwatch.Frequency,10:F3}");
            }

            Console.WriteLine($"{rows[r].Name,-42} " + string.Join(" ", cells));
        }

        Console.WriteLine();
        Console.WriteLine($"{"relative to baseline",-42}");

        for (var r = 1; r < rows.Count; r++)
        {
            var cells = new List<string>();

            for (var g = 0; g < groups.Length; g++)
            {
                cells.Add(groups[g].Length == 0 ? $"{"-",10}" : $"{(double)best[r, g] / best[0, g] - 1,10:+0.0%;-0.0%}");
            }

            Console.WriteLine($"{rows[r].Name,-42} " + string.Join(" ", cells));
        }

        Console.WriteLine();
        Console.WriteLine("ms per pass over the streams of the size class, the best of interleaved rounds.");
    }

    private static bool InBucket(int length, int index)
    {
        var lower = index == 0 ? 0 : Buckets[index - 1].Limit;
        return length > lower && length <= Buckets[index].Limit;
    }

    private static void RunBaseline(int[] group, Memory<byte>[] streams, byte[][] buffers)
    {
        foreach (var s in group)
        {
            InflaterBaseline.Inflate(streams[s].Span, ref buffers[s], out _);
        }
    }

    private static void RunCurrent(int[] group, Memory<byte>[] streams, byte[][] buffers)
    {
        foreach (var s in group)
        {
            Inflater.Inflate(streams[s].Span, ref buffers[s], out _);
        }
    }

    /// <summary>The filter's buffer handling around the baseline inflater, as the filter had it at HEAD.</summary>
    private static void BaselineWithPlumbing(int[] group, Memory<byte>[] streams)
    {
        foreach (var s in group)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, Math.Min(MaximumCapacity, streams[s].Length * 4)));
            var kept = false;

            try
            {
                var length = InflaterBaseline.Inflate(streams[s].Span, ref buffer, out _);
                kept = Keep(buffer, length);
            }
            finally
            {
                if (!kept)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }

    /// <summary>The filter's buffer handling around the current inflater, as the filter has it now.</summary>
    private static void CurrentWithPlumbing(int[] group, Memory<byte>[] streams)
    {
        foreach (var s in group)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, Math.Min(MaximumCapacity, streams[s].Length * 4)));
            var kept = false;

            try
            {
                var length = Inflater.Inflate(streams[s].Span, ref buffer, out _);
                kept = Keep(buffer, length);
            }
            finally
            {
                if (!kept)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }

    /// <summary>The filter's rule: a buffer at least three quarters full is the result, anything else is copied into an exact array.</summary>
    private static bool Keep(byte[] buffer, int length)
    {
        if (length > 0 && length >= buffer.Length - (buffer.Length >> 2))
        {
            Sink = buffer;
            return true;
        }

        var exact = GC.AllocateUninitializedArray<byte>(length);
        buffer.AsSpan(0, length).CopyTo(exact);
        Sink = exact;
        return false;
    }

    /// <summary>Where results go, so that they are not optimised away.</summary>
    private static byte[]? Sink;
}
