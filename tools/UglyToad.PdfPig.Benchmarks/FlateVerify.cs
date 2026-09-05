using System.Diagnostics;
using System.Reflection;
using UglyToad.PdfPig.Filters;

namespace UglyToad.PdfPig.Benchmarks;

/// <summary>
/// Checks the managed inflater against DeflateStream over the Flate streams of many documents:
/// every stream is decoded by the filter both ways and the results compared byte for byte. A
/// damaged stream is allowed to come out longer from the managed inflater, which keeps what
/// decoded up to the damage where DeflateStream drops the read that met it; the native result
/// then has to be a prefix. Anything else is a mismatch and is listed.
/// </summary>
/// <remarks>
/// Run with <c>dotnet run -c Release -f net8.0 -- flate-verify folder [max-files [stride]]</c>.
/// </remarks>
internal static class FlateVerify
{
    public static void Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("flate-verify folder [max-files [stride]]");
            return;
        }

        var files = FlateStreams.Files(args[1], args.Length > 2 ? int.Parse(args[2]) : int.MaxValue, args.Length > 3 ? int.Parse(args[3]) : 1);

        var filter = new FlateFilter();
        var managed = typeof(FlateFilter).GetField("UseManagedInflater", BindingFlags.NonPublic | BindingFlags.Static)!;

        long documents = 0, unopened = 0, streams = 0, identical = 0, longer = 0, mismatches = 0, nativeFailed = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (var file in files)
        {
            documents++;

            PdfDocument document;

            try
            {
                document = PdfDocument.Open(file, new ParsingOptions { UseLenientParsing = true });
            }
            catch
            {
                unopened++;
                continue;
            }

            using (document)
            {
                foreach (var stream in FlateStreams.Streams(document, includePredictors: true))
                {
                    streams++;

                    managed.SetValue(null, false);
                    Memory<byte> expected;

                    try
                    {
                        expected = filter.Decode(stream.Data, stream.StreamDictionary, DefaultFilterProvider.Instance, 0);
                    }
                    catch (Exception e)
                    {
                        // The native path gives up on some damage the managed one reads past; only
                        // counted, since there is nothing to compare against.
                        nativeFailed++;
                        Console.WriteLine($"native failed: {file}, stream of {stream.Data.Length} bytes: {e.GetType().Name}");
                        continue;
                    }

                    managed.SetValue(null, true);
                    var actual = filter.Decode(stream.Data, stream.StreamDictionary, DefaultFilterProvider.Instance, 0);

                    if (actual.Span.SequenceEqual(expected.Span))
                    {
                        identical++;
                    }
                    else if (actual.Length > expected.Length && actual.Span.Slice(0, expected.Length).SequenceEqual(expected.Span))
                    {
                        longer++;
                    }
                    else
                    {
                        mismatches++;
                        var common = expected.Span.CommonPrefixLength(actual.Span);
                        Console.WriteLine($"MISMATCH: {file}, stream of {stream.Data.Length} bytes: native {expected.Length} bytes, managed {actual.Length}, first difference at {common}");
                    }
                }
            }

            if (documents % 200 == 0)
            {
                Console.WriteLine($"{documents} documents, {streams} streams, {mismatches} mismatches, {stopwatch.Elapsed:mm\\:ss}");
            }
        }

        managed.SetValue(null, true);

        Console.WriteLine();
        Console.WriteLine($"{documents} documents ({unopened} could not be opened), {streams} Flate streams in {stopwatch.Elapsed:mm\\:ss}");
        Console.WriteLine($"{identical} identical, {longer} longer than the native result with it as prefix (damaged streams), {nativeFailed} the native path failed on, {mismatches} mismatches");
    }
}
