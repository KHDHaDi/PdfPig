using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Benchmarks;

/// <summary>
/// The Flate streams of real documents, pulled out once for the benchmarks that measure
/// inflating them.
/// </summary>
internal static class FlateStreams
{
    /// <summary>The documents the benchmarks ship with.</summary>
    public static readonly string[] Documents =
    [
        "Pig Production Handbook.pdf", "fseprd1102849.pdf", "MOZILLA-7375-0.pdf", "iron-ore-q2-q3-2013.pdf", "11194059_2017-11_de_s.pdf", "algo.pdf"
    ];

    /// <summary>The plain Flate streams, without predictors, of the documents the benchmarks ship with.</summary>
    public static (byte[] Data, DictionaryToken Dictionary)[] Load() => Load(Documents, includePredictors: false);

    /// <summary>
    /// The Flate streams of <paramref name="files"/>: those whose only filter is FlateDecode, and
    /// only those without decode parameters unless <paramref name="includePredictors"/> is set.
    /// Documents that cannot be opened are skipped.
    /// </summary>
    public static (byte[] Data, DictionaryToken Dictionary)[] Load(IEnumerable<string> files, bool includePredictors)
    {
        var found = new List<(byte[], DictionaryToken)>();

        foreach (var file in files)
        {
            PdfDocument document;

            try
            {
                document = PdfDocument.Open(file, new ParsingOptions { UseLenientParsing = true });
            }
            catch
            {
                continue;
            }

            using (document)
            {
                foreach (var stream in Streams(document, includePredictors))
                {
                    found.Add((stream.Data.ToArray(), stream.StreamDictionary));
                }
            }
        }

        return found.ToArray();
    }

    /// <summary>The Flate streams of an open document, as <see cref="Load(IEnumerable{string}, bool)"/> selects them.</summary>
    public static IEnumerable<StreamToken> Streams(PdfDocument document, bool includePredictors)
    {
        foreach (var reference in document.Structure.CrossReferenceTable.ObjectOffsets.Keys.ToList())
        {
            ObjectToken obj;

            try
            {
                obj = document.Structure.GetObject(reference);
            }
            catch
            {
                continue;
            }

            if (obj.Data is not StreamToken stream
                || !stream.StreamDictionary.TryGet(NameToken.Filter, out var token)
                || token is not NameToken name
                || name.Data != NameToken.FlateDecode.Data
                || (!includePredictors && stream.StreamDictionary.TryGet(NameToken.DecodeParms, out _))
                || stream.Data.Length < 3)
            {
                continue;
            }

            yield return stream;
        }
    }

    /// <summary>
    /// The PDF files under <paramref name="folder"/>, every <paramref name="stride"/>th of them in
    /// directory order and at most <paramref name="maximum"/>, so that a sample spreads over a
    /// large corpus rather than taking its first files.
    /// </summary>
    public static IEnumerable<string> Files(string folder, int maximum, int stride)
    {
        var taken = 0;
        var seen = 0;

        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories))
        {
            if (seen++ % stride != 0)
            {
                continue;
            }

            if (taken++ >= maximum)
            {
                yield break;
            }

            yield return file;
        }
    }
}
