namespace UglyToad.PdfPig.Tests.Filters
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;
    using Integration;
    using PdfPig.Filters.Flate;
    using PdfPig.Tokens;

    public class InflaterTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var level in new[] { CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.NoCompression })
            {
                foreach (var kind in new[] { "text", "random", "zeros", "mixed", "pixels" })
                {
                    foreach (var size in new[] { 0, 1, 7, 258, 4096, 70_000, 400_000 })
                    {
                        yield return new object[] { level, kind, size };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void RoundTripsWhatDeflateStreamCompressed(CompressionLevel level, string kind, int size)
        {
            var original = Sample(kind, size);
            var compressed = Compress(original, level);

            var (decoded, outcome) = Inflate(compressed);

            // The .NET Framework compressor writes nothing at all for no input; that is no deflate
            // stream, so it counts as cut short rather than complete.
            Assert.Equal(compressed.Length == 0 ? Inflater.Outcome.Truncated : Inflater.Outcome.Complete, outcome);
            Assert.Equal(original, decoded);
        }

        [Fact]
        public void EmptyInputIsTruncatedAndYieldsNothing()
        {
            var (decoded, outcome) = Inflate(Array.Empty<byte>());

            Assert.Equal(Inflater.Outcome.Truncated, outcome);
            Assert.Empty(decoded);
        }

        [Fact]
        public void TruncatedInputKeepsThePrefixThatDecoded()
        {
            var original = Sample("text", 300_000);
            var compressed = Compress(original, CompressionLevel.Optimal);
            var half = compressed.Take(compressed.Length / 2).ToArray();

            var (decoded, outcome) = Inflate(half);

            Assert.Equal(Inflater.Outcome.Truncated, outcome);
            Assert.NotEmpty(decoded);
            Assert.True(decoded.Length < original.Length);
            Assert.Equal(original.AsSpan(0, decoded.Length).ToArray(), decoded);
        }

        [Fact]
        public void DamagedInputKeepsWhatDecodedBeforeTheDamage()
        {
            var original = Sample("text", 300_000);
            var compressed = Compress(original, CompressionLevel.Optimal);

            var damage = (compressed.Length * 3) / 5;
            compressed[damage] ^= 0xFF;
            compressed[damage + 1] ^= 0xFF;

            var (decoded, outcome) = Inflate(compressed);
            var (reference, referenceFailed) = DecompressWithDeflateStream(compressed);

            // Damage is noticed a little after it is met, and only where the bits stop forming a
            // valid stream, which for some damage never happens. What has to hold is that this
            // inflater sees exactly what zlib sees: the same failure, or the same complete output.
            if (referenceFailed)
            {
                Assert.NotEqual(Inflater.Outcome.Complete, outcome);

                var half = original.Length / 2;
                Assert.True(decoded.Length >= half, $"only {decoded.Length} of {original.Length} bytes survived");
                Assert.Equal(original.AsSpan(0, half).ToArray(), decoded.AsSpan(0, half).ToArray());
                Assert.Equal(reference, decoded.AsSpan(0, reference.Length).ToArray());
            }
            else
            {
                Assert.Equal(Inflater.Outcome.Complete, outcome);
                Assert.Equal(reference, decoded);
            }
        }

        [Fact]
        public void InvalidBlockTypeIsDamaged()
        {
            // Block type 3 in the first byte.
            var (decoded, outcome) = Inflate([0x07, 0x00, 0x00]);

            Assert.Equal(Inflater.Outcome.Damaged, outcome);
            Assert.Empty(decoded);
        }

        [Fact]
        public void StoredBlockWithWrongComplementIsDamaged()
        {
            // Stored, final; length 3 but complement of 4.
            var (_, outcome) = Inflate([0x01, 0x03, 0x00, 0xFB, 0xFF, 1, 2, 3]);

            Assert.Equal(Inflater.Outcome.Damaged, outcome);
        }

        [Fact]
        public void MatchesTheFrameworkInflaterOnEveryFlateStreamOfTheTestDocuments()
        {
            var folder = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Integration", "Documents"));
            var compared = 0;

            foreach (var file in Directory.GetFiles(folder, "*.pdf"))
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
                    foreach (var reference in document.Structure.CrossReferenceTable.ObjectOffsets.Keys.ToList())
                    {
                        StreamToken? stream;
                        try
                        {
                            stream = document.Structure.GetObject(reference).Data as StreamToken;
                        }
                        catch
                        {
                            continue;
                        }

                        if (stream is null
                            || !stream.StreamDictionary.TryGet(NameToken.Filter, out var token)
                            || token is not NameToken name
                            || name.Data != NameToken.FlateDecode.Data
                            || stream.Data.Length < 3)
                        {
                            continue;
                        }

                        var (expected, frameworkFailed) = DecompressWithDeflateStream(stream.Data.Span.Slice(2).ToArray());

                        var (decoded, outcome) = Inflate(stream.Data.Span.Slice(2).ToArray());

                        var where = $"{Path.GetFileName(file)} object {reference.ObjectNumber}: {expected.Length} bytes expected, {decoded.Length} decoded";

                        if (frameworkFailed)
                        {
                            // The framework inflater loses the read that met the damage, so it stops at
                            // a block boundary before it; this one stops at the damage itself.
                            Assert.True(outcome == Inflater.Outcome.Damaged, where + ", outcome " + outcome);
                            Assert.True(decoded.Length >= expected.Length, where);
                            Assert.True(expected.AsSpan().SequenceEqual(decoded.AsSpan(0, expected.Length)), where);
                        }
                        else
                        {
                            Assert.True(expected.AsSpan().SequenceEqual(decoded), where);
                        }

                        compared++;
                    }
                }
            }

            Assert.True(compared > 1000, $"only {compared} streams compared");
        }

        private static (byte[] Decoded, Inflater.Outcome Outcome) Inflate(byte[] compressed)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1024);

            try
            {
                var written = Inflater.Inflate(compressed, ref buffer, out var outcome);

                return (buffer.AsSpan(0, written).ToArray(), outcome);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static byte[] Compress(byte[] data, CompressionLevel level)
        {
            using var output = new MemoryStream();

            using (var deflate = new DeflateStream(output, level, leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }

        private static (byte[] Decoded, bool Failed) DecompressWithDeflateStream(byte[] compressed)
        {
            using var source = new MemoryStream(compressed);
            using var deflate = new DeflateStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();

            var block = new byte[8192];
            while (true)
            {
                int read;
                try
                {
                    read = deflate.Read(block, 0, block.Length);
                }
                catch (InvalidDataException)
                {
                    return (output.ToArray(), true);
                }

                if (read == 0)
                {
                    break;
                }

                output.Write(block, 0, read);
            }

            return (output.ToArray(), false);
        }

        private static byte[] Sample(string kind, int size)
        {
            var random = new Random(size + kind.Length);
            var data = new byte[size];

            switch (kind)
            {
                case "text":
                    var text = new StringBuilder();
                    for (var i = 0; text.Length < size; i++)
                    {
                        text.Append("BT /F1 9 Tf 1 0 0 1 ").Append(i * 7 % 500).Append(' ').Append(i * 13 % 800).Append(" Tm (Line ").Append(i).Append(") Tj ET\n");
                    }

                    return Encoding.ASCII.GetBytes(text.ToString(0, size));
                case "random":
                    random.NextBytes(data);
                    return data;
                case "zeros":
                    return data;
                case "mixed":
                    for (var i = 0; i < size; i++)
                    {
                        data[i] = (i / 1000) % 3 == 0 ? (byte)random.Next(256) : (byte)(i % 7 == 0 ? 0 : 'a' + (i % 26));
                    }

                    return data;
                default:
                    // Image-like: smooth gradients with noise, three bytes per pixel.
                    for (var i = 0; i < size; i++)
                    {
                        data[i] = (byte)((i / 3 % 640) / 4 + (i % 3) * 40 + random.Next(6));
                    }

                    return data;
            }
        }
    }
}
