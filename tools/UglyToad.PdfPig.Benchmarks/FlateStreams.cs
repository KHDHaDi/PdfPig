using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Benchmarks;

/// <summary>
/// The plain Flate streams of several real documents, pulled out once for the benchmarks that
/// measure inflating them.
/// </summary>
internal static class FlateStreams
{
    public static (byte[] Data, DictionaryToken Dictionary)[] Load()
    {
        var found = new List<(byte[], DictionaryToken)>();

        foreach (var file in new[] { "Pig Production Handbook.pdf", "fseprd1102849.pdf", "MOZILLA-7375-0.pdf", "iron-ore-q2-q3-2013.pdf", "11194059_2017-11_de_s.pdf", "algo.pdf" })
        {
            using var document = PdfDocument.Open(file, new ParsingOptions { UseLenientParsing = true });

            foreach (var reference in document.Structure.CrossReferenceTable.ObjectOffsets.Keys)
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
                    || stream.StreamDictionary.TryGet(NameToken.DecodeParms, out _)
                    || stream.Data.Length < 3)
                {
                    continue;
                }

                found.Add((stream.Data.ToArray(), stream.StreamDictionary));
            }
        }

        return found.ToArray();
    }
}
