using System.Linq;
namespace UglyToad.PdfPig.Tests.Filters
{
    using CcittFax = UglyToad.PdfPig.Filters.CcittFax;
    using System.Globalization;
    using System.Text;
    using UglyToad.PdfPig.Core;
    using UglyToad.PdfPig.Filters;
    using UglyToad.PdfPig.Fonts;
    using UglyToad.PdfPig.Tests.Images;
    using UglyToad.PdfPig.Tokens;

    /// <summary>Verifies CCITT parameter resolution, allocation bounds and document-scoped parsing policy.</summary>
    /// <remarks>
    /// Synthetic PDF streams exercise the public filter and filter chains with attacker-controlled
    /// dimensions. Unsafe dimensions must be rejected before a large allocation; lenient recovery
    /// must not bypass that limit. Header, polarity and slice cases specify output directly.
    /// <para>Termination tests construct eight-pixel rows from readable bit strings. EOL is
    /// 000000000001; six EOLs form Group 3 RTC, two form Group 4 EOFB. Mixed Group 3 adds a
    /// 1D tag of 1 after each RTC EOL. EOF is simply the end of the supplied byte array.
    /// Shortened RTC/EOFB and declared-Rows EOF tests specify local PdfPig compatibility
    /// policy; they do not claim that incomplete markers satisfy the standard marker format.</para>
    /// <para>Height variations select different execution paths: zero bypasses compact bitmap
    /// decoding; positive Height bounds stored image rows while excess rows are still decoded; an oversized
    /// Height does not synthesize missing rows. Changing Height must not change encoded
    /// termination policy or consumption. Direct TryDecodeBitmap assertions separately prove fast-path
    /// success without relying on an unnoticed growing fallback.</para>
    /// <para>With BlackIs1=true an all-white eight-pixel row is 0x00; with false it is 0xFF.
    /// Tests assert rows, statuses and byte consumption separately: correct pixels alone
    /// cannot prove correct recognition of the image ending or exclusion of following data.</para>
    /// </remarks>
    public class CcittFaxDecodeFilterTests
    {
        [Theory]
        [InlineData(0, 1)]
        [InlineData(-1, 1)]
        [InlineData(1, -1)]
        [InlineData(1728, int.MaxValue)]
        [InlineData(int.MaxValue, 1)]
        [InlineData(int.MaxValue, int.MaxValue)]
        [InlineData(40_000_000, 1)] // Small bitmap, but the change arrays exceed the budget.
        public void RejectsUnsafeDimensionsBeforeReadingInput(int columns, int rows)
        {
            var parameters = new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Columns, new NumericToken(columns) },
                { NameToken.Rows, new NumericToken(rows) },
                { NameToken.Create("EndOfBlock"), BooleanToken.False }
            });
            var dictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Filter, NameToken.CcittfaxDecode },
                { NameToken.DecodeParms, parameters }
            });

            // Unsafe dimensions must be rejected even when there is nothing to decode.
            Assert.Throws<CorruptCompressedDataException>(() =>
                new CcittFaxDecodeFilter(useLenientParsing: false).Decode(Memory<byte>.Empty, dictionary, TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ValidatesTheEffectiveHeight(bool inlineHeight)
        {
            var parameters = new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Columns, new NumericToken(1728) },
                { NameToken.Rows, new NumericToken(1) }
            });
            var dictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Filter, NameToken.CcittfaxDecode },
                { NameToken.DecodeParms, parameters },
                { inlineHeight ? NameToken.H : NameToken.Height, new NumericToken(int.MaxValue) }
            });

            Assert.Throws<CorruptCompressedDataException>(() =>
                new CcittFaxDecodeFilter(useLenientParsing: false).Decode(Memory<byte>.Empty, dictionary, TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-1, 1)]
        [InlineData(1, 0)]
        [InlineData(1, -1)]
        public void DefaultFilterToleratesInvalidDimensions(int columns, int rows)
        {
            var parameters = new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Columns, new NumericToken(columns) },
                { NameToken.Rows, new NumericToken(rows) }
            });
            var dictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Filter, NameToken.CcittfaxDecode },
                { NameToken.DecodeParms, parameters }
            });
            Assert.True(new CcittFaxDecodeFilter().Decode(Memory<byte>.Empty, dictionary, TestFilterProvider.Instance, 0).IsEmpty);
        }

        [Theory]
        [InlineData(0, 1, false)]
        [InlineData(-1, 1, false)]
        [InlineData(1, -1, false)]
        [InlineData(1, int.MinValue, false)]
        [InlineData(0, 1, true)]
        [InlineData(1, -1, true)]
        public void ParsingOptionsControlDimensionValidation(int columns, int rows, bool explicitProvider)
        {
            var pdf = CreateAllocationBombPdf(columns, rows, filterChain: true);
            var strictOptions = new ParsingOptions
            {
                UseLenientParsing = false,
                FilterProvider = explicitProvider ? DefaultFilterProvider.Instance : null
            };
            using var strictDocument = PdfDocument.Open(pdf, strictOptions);
            var strictStream = (StreamToken)strictDocument.Structure.GetObject(new IndirectReference(5, 0)).Data;
            var exception = Assert.Throws<CorruptCompressedDataException>(() =>
                strictStream.Decode(strictDocument.Structure.FilterProvider, strictDocument.Structure.TokenScanner));
            Assert.StartsWith("Invalid CCITT image dimensions:", exception.Message);

            // Keep both documents alive: strict options must not alter the shared default filter.
            using var lenientDocument = PdfDocument.Open(pdf, new ParsingOptions
            {
                FilterProvider = explicitProvider ? DefaultFilterProvider.Instance : null
            });
            var lenientStream = (StreamToken)lenientDocument.Structure.GetObject(new IndirectReference(5, 0)).Data;
            Assert.True(lenientStream.Decode(lenientDocument.Structure.FilterProvider, lenientDocument.Structure.TokenScanner).IsEmpty);
            Assert.True(lenientStream.Decode(DefaultFilterProvider.Instance).IsEmpty);
            Assert.Throws<CorruptCompressedDataException>(() =>
                strictStream.Decode(strictDocument.Structure.FilterProvider, strictDocument.Structure.TokenScanner));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void LenientDimensionsCannotCancelTheWorkingBufferLimit(int rows)
        {
            var exception = Assert.Throws<CorruptCompressedDataException>(() =>
                CcittFaxDecodeFilter.GetDecodedBufferSize(40_000_000, rows, useLenientParsing: true));
            Assert.StartsWith("CCITT decode buffers require ", exception.Message);
        }

        [Fact]
        public void IncludesWorkingBuffersAtTheLimit()
        {
            // A one-pixel row occupies one byte. The conservative working-buffer estimate
            // adds one row byte and two three-entry Int32 arrays: 25 bytes beyond the bitmap.
            var lastAllowedRowCount = (int)CcittFaxDecodeFilter.MaximumDecodeBufferBytes - 25;
            Assert.Equal(lastAllowedRowCount, CcittFaxDecodeFilter.GetDecodedBufferSize(1, lastAllowedRowCount));
            Assert.Throws<CorruptCompressedDataException>(() =>
                CcittFaxDecodeFilter.GetDecodedBufferSize(1, lastAllowedRowCount + 1));
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(8, 3, 3)]
        [InlineData(9, 3, 6)]
        [InlineData(1800, 3113, 700425)]
        public void CalculatesRoundedBitmapSize(int columns, int rows, int expected)
        {
            Assert.Equal(expected, CcittFaxDecodeFilter.GetDecodedBufferSize(columns, rows));
        }

        [Theory]
        [InlineData(1728, 2_000_000, false, false)]
        [InlineData(1728, 2_000_000, false, true)]
        [InlineData(40_000_000, 1, true, false)]
        [InlineData(40_000_000, 1, true, true)]
        public void RejectsAllocationBombInPageImage(int columns, int rows, bool filterChain, bool lenient)
        {
            // The dimensions require either a 432,000,000-byte bitmap or two roughly
            // 160,000,000-byte transition arrays with only a 5,000,000-byte bitmap.
            // A one-byte CCITT payload must be rejected before allocating those buffers.
            var pdf = CreateAllocationBombPdf(columns, rows, filterChain);
            Assert.InRange(pdf.Length, 1, 1024);

            using var document = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = lenient });
            var image = Assert.Single(document.GetPage(1).GetImages());
            Assert.Equal(filterChain ? 3 : 1, image.RawMemory.Length);

#if NET
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
#endif
            var exception = Assert.Throws<CorruptCompressedDataException>(() => image.TryGetBytesAsMemory(out _));
#if NET
            // Count allocations after parsing the PDF and locating its image. This measures
            // the cost of rejecting the image, rather than the cost of reading the document.
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, 0L, 1024L * 1024);
#endif
            Assert.StartsWith("CCITT decode buffers require ", exception.Message);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(-1, false)]
        [InlineData(-1, true)]
        public void EmptyInputRespectsParsingMode(int k, bool lenient)
        {
            var dictionary = CreateSmallImageDictionary(k);
            var filter = new CcittFaxDecodeFilter(lenient);
            if (lenient)
            {
                Assert.True(filter.Decode(Memory<byte>.Empty, dictionary, TestFilterProvider.Instance, 0).IsEmpty);
            }
            else
            {
                var exception = Assert.Throws<CorruptCompressedDataException>(() =>
                    filter.Decode(Memory<byte>.Empty, dictionary, TestFilterProvider.Instance, 0));
                Assert.Equal("Empty CCITT compressed data.", exception.Message);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        [InlineData(null)]
        public void DecodesShortWhiteRun(bool? endOfLine)
        {
            // The T.4 code 10011 represents eight white pixels. EndOfLine=true adds
            // a leading EOL (000000000001); false or absent uses the code alone.
            var input = endOfLine == true ? new byte[] { 0x00, 0x19, 0x80 } : new byte[] { 0x98 };
            var output = new CcittFaxDecodeFilter().Decode(input, CreateSmallImageDictionary(0, endOfLine), TestFilterProvider.Instance, 0);
            Assert.Equal(new byte[] { 0x00 }, output.ToArray());
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(19)]
        [InlineData(20)]
        public void HeaderSearchStaysWithinAvailableInput(int length)
        {
            var input = new byte[length];
            input[0] = 0x98; // One complete white run followed by padding with no EOL.
            var output = new CcittFaxDecodeFilter().Decode(input, CreateSmallImageDictionary(0), TestFilterProvider.Instance, 0);
            Assert.Equal(new byte[] { 0x00 }, output.ToArray());
        }

        [Theory]
        [InlineData(false, 0x00)]
        [InlineData(true, 0xFF)]
        /// <remarks>
        /// Distinguishes an explicit required row EOL from an optional EOL when EndOfLine is false or
        /// absent. The first real row must not be skipped during framing.
        /// </remarks>
        [InlineData(null, 0x00)]
        public void RequiredEndOfLineControlsInitialRowFraming(bool? endOfLine, byte expected)
        {
            // A white Modified Huffman row precedes an EOL and a black Group 3 row.
            // Optional EOL preserves the first row, including when the parameter is absent.
            // The required-EOL case supplies its marker directly, without scanning past another row.
            var input = endOfLine == true ? PackCcittBits(Eol + BlackEight)
                : new byte[] { 0x98, 0x00, 0x13, 0x51, 0x40 };
            var output = new CcittFaxDecodeFilter().Decode(input, CreateSmallImageDictionary(0, endOfLine), TestFilterProvider.Instance, 0);
            Assert.Equal(new[] { expected }, output.ToArray());
        }

        // Hand-built test vectors using standard T.4 run/EOL codes and T.6 mode codes.
        // Framing and damaged-row expectations follow PDF Reference 1.7, section 3.3.5:
        // https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.7old.pdf
        // These vectors were created for these tests; they were not copied from another project.
        // InvalidRun deliberately inserts a malformed run prefix to exercise recovery.
        private const string Eol = "000000000001";
        private const string WhiteEight = "10011";
        private const string BlackEight = "00110101000101";
        private const string InvalidRun = "0000000001";

        /// <summary>Optional EOL is framing, not a full-width white run, for K = 0.</summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        /// <remarks>
        /// A row-introducing EOL is framing, not a full-width white run. Both row implementations must
        /// preserve the black pixels and polarity.
        /// </remarks>
        [InlineData(true, true)]
        public void OptionalEolPreservesBlackRowsInBothPaths(bool lenient, bool blackIsOne)
        {
            var input = PackCcittBits(Eol + BlackEight + Eol + WhiteEight);
            var expected = blackIsOne ? new byte[] { 255, 0 } : new byte[] { 0, 255 };
            var dictionary = CreateFramedDictionary(0, 2, false, false, blackIsOne);
            Assert.Equal(expected, new CcittFaxDecodeFilter(lenient).Decode(input, dictionary, TestFilterProvider.Instance, 0).ToArray());
            var signed = new byte[2];
            CcittFax.CcittFaxCompactDecoder.DecodeCompatibility(input, signed, 8, 2,
                CcittFax.CcittFaxCompressionType.ModifiedHuffman, false, blackIsOne, lenient, true, false);
            Assert.Equal(expected, signed);
            var compact = new byte[2];
            Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecode(input, compact, 8, 2,
                CcittFax.CcittFaxCompressionType.ModifiedHuffman, false, blackIsOne, true, false));
            Assert.Equal(expected, compact);
        }

        /// <summary>Complete EOFB/RTC terminates decoding even when valid image codes follow it.</summary>
        [Theory]
        [InlineData(-1, false)]
        [InlineData(-1, true)]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(2, false)]
        /// <remarks>
        /// A full RTC or EOFB at the start produces no image rows, despite positive Rows and later valid
        /// image codes. Full-marker precedence must hold in both parsing modes.
        /// </remarks>
        [InlineData(2, true)]
        public void CompleteEndMarkerStopsBothParsingModes(int k, bool lenient)
        {
            var rtc = string.Concat(Enumerable.Repeat(Eol + (k > 0 ? "1" : ""), 6));
            string inputBits = k < 0 ? Eol + Eol + "11111111" : rtc + BlackEight;
            var input = PackCcittBits(inputBits);
            var dictionary = CreateFramedDictionary(k, 3, k >= 0, true, true);
            Assert.Empty(new CcittFaxDecodeFilter(lenient).Decode(input, dictionary, TestFilterProvider.Instance, 0).ToArray());
            var mode = k < 0 ? CcittFax.CcittFaxCompressionType.Group4_2D
                : k > 0 ? CcittFax.CcittFaxCompressionType.Group3_2D : CcittFax.CcittFaxCompressionType.Group3_1D;
            var signed = new byte[3];
            CcittFax.CcittFaxCompactDecoder.DecodeCompatibility(input, signed, 8, 3, mode, false, true, lenient, true, true);
            Assert.Equal(new byte[3], signed);
            var compact = new byte[3];
            Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecode(input, compact, 8, 3, mode, false, true, true, true));
            Assert.Equal(new byte[3], compact);
        }

        /// <summary>A replacement row becomes the reference for subsequent mixed Group 3 rows.</summary>
        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(2, false)]
        [InlineData(2, true)]
        public void DamagedRowCopiesPreviousPixelsAndRebuildsReference(int k, bool lenient)
        {
            string tag = k > 0 ? "1" : "";
            string lastRow = k > 0 ? "0" + "11" : BlackEight;
            var input = PackCcittBits(Eol + tag + BlackEight + Eol + tag + InvalidRun + Eol + lastRow);
            var dictionary = CreateFramedDictionary(k, 3, true, false, true, 1);
            Assert.Equal(new byte[] { 255, 255, 255 }, new CcittFaxDecodeFilter(lenient).Decode(input, dictionary, TestFilterProvider.Instance, 0).ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ConsecutiveDamagedRowsUseWhiteAndEnforceBudget(bool lenient)
        {
            var input = PackCcittBits(Eol + BlackEight + Eol + InvalidRun + Eol + InvalidRun + Eol + BlackEight);
            Assert.Equal(new byte[] { 255, 255, 0, 255 }, new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(0, 4, true, false, true, 2), TestFilterProvider.Instance, 0).ToArray());
            var exception = Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(0, 4, true, false, true, 1), TestFilterProvider.Instance, 0));
            Assert.Contains("budget", exception.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DamagedFirstRowUsesWhite(bool lenient)
        {
            var input = PackCcittBits(Eol + InvalidRun + Eol + BlackEight);
            Assert.Equal(new byte[] { 0, 255 }, new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(0, 2, true, false, true, 1), TestFilterProvider.Instance, 0).ToArray());
        }

        /// <summary>Rows = 0 and absent Height decode complete rows until EOF or the end marker.</summary>
        [Theory]
        [InlineData(-1, false)]
        [InlineData(-1, true)]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(2, false)]
        /// <remarks>
        /// With unknown Rows and no Height, decode real complete rows until EOF or a marker. Missing
        /// dimensions must not invent white image rows.
        /// </remarks>
        [InlineData(2, true)]
        public void UnknownHeightDecodesOnlyCompleteRows(int k, bool lenient)
        {
            string bits = k < 0 ? "111" + Eol + Eol + "11111111"
                : Eol + (k > 0 ? "1" : "") + BlackEight + Eol + (k > 0 ? "1" : "") + WhiteEight
                    + string.Concat(Enumerable.Repeat(Eol + (k > 0 ? "1" : ""), 6)) + BlackEight;
            var expected = k < 0 ? new byte[3] : new byte[] { 255, 0 };
            var dictionary = CreateFramedDictionary(k, 0, k >= 0, true, true, includeImageHeight: false);
            Assert.Equal(expected, new CcittFaxDecodeFilter(lenient).Decode(PackCcittBits(bits), dictionary, TestFilterProvider.Instance, 0).ToArray());
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// A full end marker, rather than the declared Rows value or absent Height, decides where
        /// EndOfBlock=true decoding stops.
        /// </remarks>
        [InlineData(true)]
        public void EndOfBlockOverridesRowsWithoutImageHeight(bool lenient)
        {
            var input = PackCcittBits("111" + Eol + Eol);
            Assert.Equal(new byte[3], new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(-1, 1, false, true, true, includeImageHeight: false), TestFilterProvider.Instance, 0).ToArray());
            Assert.Equal(new byte[1], new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(-1, 1, false, false, true, includeImageHeight: false), TestFilterProvider.Instance, 0).ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnknownHeightRecoversDamagedRows(bool lenient)
        {
            var input = PackCcittBits(Eol + BlackEight + Eol + InvalidRun + Eol + WhiteEight
                + string.Concat(Enumerable.Repeat(Eol, 6)));
            Assert.Equal(new byte[] { 255, 255, 0 }, new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(0, 0, true, true, true, 1, false), TestFilterProvider.Instance, 0).ToArray());
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// An EOL encountered inside a run interrupts a damaged row. Explicit recovery uses that EOL to
        /// frame the next row instead of consuming it twice.
        /// </remarks>
        [InlineData(true)]
        public void PrematureEolRecoveryKeepsTheFollowingRow(bool lenient)
        {
            // A zero-length white run is followed by EOL instead of the missing black run.
            var input = PackCcittBits(Eol + BlackEight + Eol + "00110101" + Eol + WhiteEight);
            Assert.Equal(new byte[] { 255, 255, 0 }, new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(0, 3, true, false, true, 1), TestFilterProvider.Instance, 0).ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnknownHeightEnforcesBudgetDuringGrowth(bool lenient)
        {
            // 9,000 vertical-zero codes are 9,000 white rows. The second growth would need
            // both old and new arrays beyond 16 KiB; no production-sized allocation is needed.
            var input = Enumerable.Repeat((byte)255, 1125).ToArray();
            var exception = Assert.Throws<CorruptCompressedDataException>(() =>
                CcittFax.CcittFaxCompactDecoder.DecodeRowsToMemory(input, 8,
                    CcittFax.CcittFaxCompressionType.Group4_2D, false, true, lenient, true, false, 0, 16384, requireEndOfLine: false));
            Assert.Contains("allocation budget", exception.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnknownHeightHandlesIncompleteRowsAccordingToParsingMode(bool blackIsOne)
        {
            var input = PackCcittBits(BlackEight + WhiteEight + "00110101");
            var expected = blackIsOne ? new byte[] { 255, 0 } : new byte[] { 0, 255 };
            var dictionary = CreateFramedDictionary(0, 0, false, false, blackIsOne, includeImageHeight: false);
            // No hint selects signed decoding directly; positive hints exercise compact failure
            // followed by signed recovery. Neither route may return an incomplete final row.
            foreach (int height in new[] { 0, 1, 3 })
            {
                var hinted = dictionary.With(NameToken.Height, new NumericToken(height));
                Assert.Equal(expected.Take(height > 0 ? height : expected.Length).ToArray(), new CcittFaxDecodeFilter(true).Decode(input, hinted, TestFilterProvider.Instance, 0).ToArray());
                Assert.Throws<CorruptCompressedDataException>(() =>
                    new CcittFaxDecodeFilter(false).Decode(input, hinted, TestFilterProvider.Instance, 0));
            }
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// The damage budget cannot authorize recovery when no subsequent EOL establishes the next row
        /// boundary.
        /// </remarks>
        [InlineData(true)]
        public void MissingResynchronizationMarkerIsAnError(bool lenient)
        {
            var input = PackCcittBits(Eol + InvalidRun);
            var exception = Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(0, 1, true, false, true, 1), TestFilterProvider.Instance, 0));
            Assert.Contains("resynchronize", exception.Message);
        }

        [Fact]
        public void NegativeDamageBudgetRespectsParsingMode()
        {
            var input = PackCcittBits(Eol + WhiteEight);
            var dictionary = CreateFramedDictionary(0, 1, true, false, true, -1);
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false).Decode(input, dictionary, TestFilterProvider.Instance, 0));
            Assert.Equal(new byte[1], new CcittFaxDecodeFilter(true).Decode(input, dictionary, TestFilterProvider.Instance, 0).ToArray());
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// The bit after optional EOL is a 1D/2D tag, not image pixels. Alternating framed and unframed rows
        /// must retain their intended coding mode.
        /// </remarks>
        [InlineData(true)]
        public void MixedGroup3AcceptsRowsWithAndWithoutOptionalEol(bool lenient)
        {
            // 1D black row, unframed 2D repetition, framed 1D white row.
            var input = PackCcittBits("1" + BlackEight + "0" + "11" + Eol + "1" + WhiteEight);
            var expected = new byte[] { 255, 255, 0 };
            Assert.Equal(expected, new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(2, 3, false, false, true), TestFilterProvider.Instance, 0).ToArray());
            var signed = new byte[3];
            CcittFax.CcittFaxCompactDecoder.DecodeCompatibility(input, signed, 8, 3,
                CcittFax.CcittFaxCompressionType.Group3_2D, false, true, lenient,
                acceptOptionalEndOfLine: true, endOfBlock: false, requireEndOfLine: false);
            Assert.Equal(expected, signed);
            var compact = new byte[3];
            Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecode(input, compact, 8, 3,
                CcittFax.CcittFaxCompressionType.Group3_2D, false, true,
                acceptOptionalEndOfLine: true, endOfBlock: false, requireEndOfLine: false));
            Assert.Equal(expected, compact);
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// With EndOfBlock disabled, positive Rows is an actual stop condition. Larger Height reserves
        /// capacity but must not decode extra rows.
        /// </remarks>
        [InlineData(true)]
        public void EndOfBlockFalseUsesRowsEvenWhenImageHeightIsLarger(bool lenient)
        {
            var dictionary = CreateFramedDictionary(-1, 1, false, false, true).With(NameToken.Height, new NumericToken(3));
            Assert.Equal(new byte[1], new CcittFaxDecodeFilter(lenient).Decode(PackCcittBits("111"),
                dictionary, TestFilterProvider.Instance, 0).ToArray());
        }

        /// <summary>Group 4 accepts optional EOL and rejects missing required EOL in strict parsing.</summary>
        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// Group 4 two-dimensional mode coding does not erase the PDF EndOfLine parameter. Test required row
        /// framing independently of EOFB termination.
        /// </remarks>
        [InlineData(true)]
        public void Group4HonorsEndOfLine(bool lenient)
        {
            foreach (bool required in new[] { false, true })
            {
                var dictionary = CreateFramedDictionary(-1, 2, required, false, true);
                var input = PackCcittBits(Eol + "001" + BlackEight + Eol + "11");
                Assert.Equal(new byte[] { 255, 255 }, new CcittFaxDecodeFilter(lenient).Decode(input,
                    dictionary, TestFilterProvider.Instance, 0).ToArray());
            }
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false).Decode(
                PackCcittBits("1"), CreateFramedDictionary(-1, 1, true, false, true), TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// A premature EOL during mixed 2D coding must remain available to explicit damaged-row recovery,
        /// preserving the following complete row.
        /// </remarks>
        [InlineData(true)]
        public void PrematureEolInTwoDimensionalRowPreservesNextRow(bool lenient)
        {
            var input = PackCcittBits(Eol + "1" + WhiteEight + Eol + "0" + Eol + "1" + BlackEight);
            Assert.Equal(new byte[] { 0, 0, 255 }, new CcittFaxDecodeFilter(lenient).Decode(input,
                CreateFramedDictionary(2, 3, true, false, true, 1), TestFilterProvider.Instance, 0).ToArray());
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// Height limits image output, not filter termination. Small heights discard output rows
        /// while still checking the marker; oversized heights never append manufactured rows.
        /// </remarks>
        [InlineData(true)]
        public void ImageHeightDoesNotOverrideFilterTermination(bool lenient)
        {
            var input = PackCcittBits("111" + Eol + Eol);
            foreach (int height in new[] { 1, 10 })
            {
                var dictionary = CreateFramedDictionary(-1, 1, false, true, true).With(NameToken.Height, new NumericToken(height));
                Assert.Equal(new byte[Math.Min(3, height)], new CcittFaxDecodeFilter(lenient).Decode(input,
                    dictionary, TestFilterProvider.Instance, 0).ToArray());
            }
            var unknownRows = CreateFramedDictionary(-1, 0, false, false, true).With(NameToken.Height, new NumericToken(1));
            Assert.Equal(new byte[1], new CcittFaxDecodeFilter(lenient).Decode(PackCcittBits("111"),
                unknownRows, TestFilterProvider.Instance, 0).ToArray());
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// Input ending after real complete rows must not be padded with manufactured image rows to satisfy
        /// Height.
        /// </remarks>
        [InlineData(true)]
        public void DataExhaustionDoesNotSynthesizeWhiteRows(bool lenient)
        {
            var dictionary = CreateFramedDictionary(-1, 10, false, false, true);
            Assert.Equal(new byte[3], new CcittFaxDecodeFilter(lenient).Decode(PackCcittBits("111"),
                dictionary, TestFilterProvider.Instance, 0).ToArray());
        }

        /// <summary>Unknown-height data may end with an incomplete RTC after complete image rows.</summary>
        [Theory]
        [InlineData(0, false, false)]
        [InlineData(0, true, true)]
        [InlineData(2, true, false)]
        /// <remarks>
        /// Rows=0 permits the existing shortened Group 3 RTC exception after a real row: two to five EOLs,
        /// correct mixed tags, and exclusively zero fill through EOF. Whole zero-fill bytes are
        /// intentionally included.
        /// </remarks>
        [InlineData(2, true, true)]
        public void UnknownRowsAcceptsIncompleteRtcAtEndOfInput(int k, bool endOfLine, bool blackIsOne)
        {
            foreach (bool lenient in new[] { false, true })
            foreach (int height in new[] { 0, 1, 10 })
            foreach (int eolCount in new[] { 2, 3, 4, 5 })
            foreach (int zeroFill in new[] { 0, 8, 65 })
            {
                string row = (endOfLine ? Eol : "") + (k > 0 ? "1" : "") + WhiteEight;
                string marker = "";
                for (int i = 0; i < eolCount; i++)
                    marker += Eol + (k > 0 ? "1" : "");
                var dictionary = CreateFramedDictionary(k, 0, endOfLine, true, blackIsOne)
                    .With(NameToken.Height, new NumericToken(height));
                var input = PackCcittBits(row + marker + new string('0', zeroFill));
                Assert.Equal(new[] { blackIsOne ? (byte)0 : (byte)255 },
                    new CcittFaxDecodeFilter(lenient).Decode(input,
                        dictionary, TestFilterProvider.Instance, 0).ToArray());
            }
        }

        [Theory]
        [InlineData(0, false)]
        /// <remarks>
        /// A candidate short RTC cannot hide nonzero data, an unfinished run, a lone Group 3 EOL, or the
        /// absence of any complete image row.
        /// </remarks>
        [InlineData(2, true)]
        public void IncompleteRtcDoesNotHideFollowingDataOrDamagedRows(int k, bool endOfLine)
        {
            string tag = k > 0 ? "1" : "";
            string row = (endOfLine ? Eol : "") + tag + WhiteEight;
            string marker = Eol + tag + Eol + tag + Eol + tag;
            var dictionary = CreateFramedDictionary(k, 0, endOfLine, true, true);
            foreach (string input in new[]
            {
                row + marker + WhiteEight, // Real data follows the candidate trailer.
                row + "00110101" + marker, // A started black run has no terminating code.
                row + Eol + tag,           // A single EOL cannot establish the exception.
                marker                    // No complete image row precedes the candidate trailer.
            })
                Assert.Throws<CorruptCompressedDataException>(() =>
                    new CcittFaxDecodeFilter(false).Decode(PackCcittBits(input),
                        dictionary, TestFilterProvider.Instance, 0));
        }

        /// <remarks>
        /// The Group 3 exception requires EndOfBlock and unknown or exactly matched Rows. A lone Group 4 EOL
        /// cannot excuse a missing declared row.
        /// </remarks>
        [Fact]
        public void IncompleteRtcExceptionRejectsMismatchedRowsAndRequiresEndOfBlock()
        {
            var input = PackCcittBits(WhiteEight + Eol + Eol + Eol);
            Assert.Throws<CorruptCompressedDataException>(() =>
                new CcittFaxDecodeFilter(false).Decode(input,
                    CreateFramedDictionary(0, 2, false, true, true), TestFilterProvider.Instance, 0));
            Assert.Throws<CorruptCompressedDataException>(() =>
                new CcittFaxDecodeFilter(false).Decode(input,
                    CreateFramedDictionary(0, 0, false, false, true), TestFilterProvider.Instance, 0));
            // A shortened Group 4 marker must not conceal a missing declared row.
            Assert.Throws<CorruptCompressedDataException>(() =>
                new CcittFaxDecodeFilter(false).Decode(PackCcittBits("1" + Eol),
                    CreateFramedDictionary(-1, 2, false, true, true), TestFilterProvider.Instance, 0));
        }

        /// <remarks>
        /// Positive Rows permits marker-free clean EOF only when its count exactly matches intact rows. A
        /// missing declared row stays a strict error; lenient output remains a prefix.
        /// </remarks>
        [Fact]
        public void StrictParsingRequiresEndMarkerUnlessDeclaredRowsMatch()
        {
            var input = PackCcittBits("1");
            var matching = CreateFramedDictionary(-1, 1, false, true, true);
            Assert.Equal(new byte[1], new CcittFaxDecodeFilter(false).Decode(input,
                matching, TestFilterProvider.Instance, 0).ToArray());
            var mismatched = CreateFramedDictionary(-1, 2, false, true, true);
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false).Decode(
                input, mismatched, TestFilterProvider.Instance, 0));
            Assert.Equal(new byte[1], new CcittFaxDecodeFilter(true).Decode(input,
                mismatched, TestFilterProvider.Instance, 0).ToArray());
        }

        /// <remarks>
        /// A premature EOL is a failed run, not a valid white row or image ending. Only an explicit damage
        /// budget may substitute pixels.
        /// </remarks>
        [Fact]
        public void ParsingDoesNotRepairPrematureRunEolWithoutBudget()
        {
            var input = PackCcittBits("00110101" + Eol);
            var dictionary = CreateFramedDictionary(0, 1, false, false, true);
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false).Decode(
                input, dictionary, TestFilterProvider.Instance, 0));
            Assert.Empty(new CcittFaxDecodeFilter(true).Decode(
                input, dictionary, TestFilterProvider.Instance, 0).ToArray());
        }

        /// <remarks>
        /// Missing required EOL must not trigger an implicit forward scan that silently drops image data.
        /// Resynchronization belongs to explicit damage recovery.
        /// </remarks>
        [Fact]
        public void RequiredEolDoesNotResynchronizeWithoutDamageBudget()
        {
            var dictionary = CreateFramedDictionary(0, 2, true, false, true);
            var input = PackCcittBits(WhiteEight + Eol + BlackEight);
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false).Decode(
                input, dictionary, TestFilterProvider.Instance, 0));
            Assert.Empty(new CcittFaxDecodeFilter(true).Decode(input, dictionary, TestFilterProvider.Instance, 0).ToArray());
        }

        [Theory]
        [InlineData(-1, false)]
        [InlineData(-1, true)]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(1, false)]
        /// <remarks>
        /// Complete markers are part of consumed image input. Bytes after the byte-rounded marker ending,
        /// including EI-like PDF syntax, remain unread despite reservoir lookahead.
        /// </remarks>
        [InlineData(1, true)]
        public void ConsumptionIncludesEndMarkerButExcludesPrefetchedFollowingBytes(int k, bool growing)
        {
            var marker = string.Concat(Enumerable.Repeat(k < 0 ? Eol : k > 0 ? Eol + "1" : Eol, k < 0 ? 2 : 6));
            var row = k < 0 ? "1" : k > 0 ? Eol + "1" + WhiteEight : WhiteEight;
            var encoded = PackCcittBits(row + marker);
            var input = encoded.Concat(new byte[] { 0x20, 0x45, 0x49, 0x20, 0x71, 0x20, 0x51 }).ToArray();
            var dictionary = CreateFramedDictionary(k, 1, k > 0, true, true, includeImageHeight: !growing);
            var pixels = new CcittFaxDecodeFilter(false).DecodeWithConsumption(input, dictionary,
                TestFilterProvider.Instance, 0, out int consumed, out var status);
            Assert.Equal(new byte[1], pixels.ToArray());
            Assert.Equal(encoded.Length, consumed);
            Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfBlock, status);
            Assert.Equal(0x20, input[consumed]);
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// EndOfBlock=false stops at positive Rows. Byte-rounded consumption includes only that row and its
        /// alignment, not the next row or trailer.
        /// </remarks>
        [InlineData(true)]
        public void ConsumptionAtRowLimitDoesNotIncludeTheNextRow(bool aligned)
        {
            var input = PackCcittBits(WhiteEight + (aligned ? "000" : "") + BlackEight + "11111111");
            var dictionary = CreateFramedDictionary(0, 1, false, false, true);
            if (aligned)
            {
                var parameters = ((DictionaryToken)dictionary.Data[NameToken.DecodeParms]).Data
                    .ToDictionary(x => NameToken.Create(x.Key), x => x.Value);
                parameters[NameToken.EncodedByteAlign] = BooleanToken.True;
                var entries = dictionary.Data.ToDictionary(x => NameToken.Create(x.Key), x => x.Value);
                entries[NameToken.DecodeParms] = new DictionaryToken(parameters);
                dictionary = new DictionaryToken(entries);
            }
            var pixels = new CcittFaxDecodeFilter(false).DecodeWithConsumption(input, dictionary,
                TestFilterProvider.Instance, 0, out int consumed, out var status);
            Assert.Equal(new byte[1], pixels.ToArray());
            Assert.Equal(1, consumed);
            Assert.Equal(CcittFax.CcittFaxDecodeStatus.RowLimit, status);
        }

        /// <remarks>
        /// When the initial compact capacity is too small, grow without losing the input position.
        /// Report the actual marker boundary and leave enclosing inline-image syntax unread.
        /// </remarks>
        [Fact]
        public void ConsumptionSurvivesDiscardingRowsBeyondImageHeight()
        {
            var encoded = PackCcittBits("11" + Eol + Eol);
            var input = encoded.Concat(new byte[] { 0x45, 0x49, 0x20, 0x51 }).ToArray();
            var dictionary = CreateFramedDictionary(-1, 1, false, true, true);
            var pixels = new CcittFaxDecodeFilter(false).DecodeWithConsumption(input, dictionary,
                TestFilterProvider.Instance, 0, out int consumed, out var status);
            Assert.Equal(new byte[1], pixels.ToArray());
            Assert.Equal(encoded.Length, consumed);
            Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfBlock, status);
        }

        /// <remarks>
        /// A successfully exhausted complete row reports EndOfInput. A failed subsequent row retained only
        /// leniently reports NotCompleted, even when earlier pixels are correct.
        /// </remarks>
        [Fact]
        public void ConsumptionDistinguishesCleanEofFromALenientFailedRow()
        {
            var dictionary = CreateFramedDictionary(0, 0, false, false, true, includeImageHeight: false);
            var filter = new CcittFaxDecodeFilter(true);
            var good = filter.DecodeWithConsumption(PackCcittBits(WhiteEight), dictionary,
                TestFilterProvider.Instance, 0, out int goodConsumed, out var goodStatus);
            Assert.Equal(new byte[1], good.ToArray());
            Assert.Equal(1, goodConsumed);
            Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfInput, goodStatus);
            var damaged = PackCcittBits(WhiteEight + "0000000000000000");
            var prefix = filter.DecodeWithConsumption(damaged, dictionary,
                TestFilterProvider.Instance, 0, out int failedConsumed, out var failedStatus);
            Assert.Equal(new byte[1], prefix.ToArray());
            Assert.InRange(failedConsumed, 1, damaged.Length);
            Assert.Equal(CcittFax.CcittFaxDecodeStatus.NotCompleted, failedStatus);
        }

        /// <remarks>
        /// Accepted short RTC consumes zero fill through EOF and reports IncompleteEndOfBlock, rather than
        /// falsely claiming a full six-EOL marker.
        /// </remarks>
        [Fact]
        public void ConsumptionIdentifiesTheIncompleteRtcCompatibilityException()
        {
            var input = PackCcittBits(WhiteEight + Eol + Eol + Eol + "00000000");
            var dictionary = CreateFramedDictionary(0, 0, false, true, true, includeImageHeight: false);
            var pixels = new CcittFaxDecodeFilter(false).DecodeWithConsumption(input, dictionary,
                TestFilterProvider.Instance, 0, out int consumed, out var status);
            Assert.Equal(new byte[1], pixels.ToArray());
            Assert.Equal(input.Length, consumed);
            Assert.Equal(CcittFax.CcittFaxDecodeStatus.IncompleteEndOfBlock, status);
        }

        [Theory]
        [InlineData(0, false, false)]
        [InlineData(0, true, true)]
        [InlineData(2, true, false)]
        /// <remarks>
        /// Two intact rows authorize a short RTC only for Rows=2. Vary tags, EOL count, polarity, zero fill
        /// and Height to exercise compact decoding and growing retries with identical results.
        /// </remarks>
        [InlineData(2, true, true)]
        public void DeclaredRowsAcceptsIncompleteRtcOnlyAfterEveryRow(int k, bool endOfLine, bool blackIsOne)
        {
            string tag = k > 0 ? "1" : "";
            string row = (endOfLine ? Eol : "") + tag + WhiteEight;
            foreach (bool lenient in new[] { false, true })
            foreach (int height in new[] { 0, 1, 2, 10 })
            foreach (int eolCount in new[] { 2, 3, 4, 5 })
            foreach (int zeroFill in new[] { 0, 8, 65 })
            {
                string marker = "";
                for (int i = 0; i < eolCount; i++) marker += Eol + tag;
                var input = PackCcittBits(row + row + marker + new string('0', zeroFill));
                var dictionary = CreateFramedDictionary(k, 2, endOfLine, true, blackIsOne)
                    .With(NameToken.Height, new NumericToken(height));
                var pixels = new CcittFaxDecodeFilter(lenient).DecodeWithConsumption(input,
                    dictionary, TestFilterProvider.Instance, 0, out int consumed, out var status);
                byte white = blackIsOne ? (byte)0 : (byte)255;
                Assert.Equal(Enumerable.Repeat(white, height == 1 ? 1 : 2).ToArray(), pixels.ToArray());
                Assert.Equal(input.Length, consumed);
                Assert.Equal(CcittFax.CcittFaxDecodeStatus.IncompleteEndOfBlock, status);
            }
        }

        [Theory]
        [InlineData(0, false)]
        /// <remarks>
        /// Matching Height or correct prefix pixels cannot justify an incorrect Rows count or nonzero data
        /// after a possible shortened RTC. Lenient output must retain a failure status.
        /// </remarks>
        [InlineData(2, true)]
        public void DeclaredRowsDoesNotTurnInvalidTrailersIntoIncompleteRtc(int k, bool endOfLine)
        {
            string tag = k > 0 ? "1" : "";
            string row = (endOfLine ? Eol : "") + tag + WhiteEight;
            string marker = Eol + tag + Eol + tag + Eol + tag;
            foreach (int height in new[] { 0, 2, 10 })
            foreach (int declaredRows in new[] { 1, 2, 3 })
            foreach (string suffix in new[] { "", "1", WhiteEight })
            {
                if (declaredRows == 2 && suffix.Length == 0) continue;
                var dictionary = CreateFramedDictionary(k, declaredRows, endOfLine, true, true)
                    .With(NameToken.Height, new NumericToken(height));
                var input = PackCcittBits(row + row + marker + suffix);
                Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                    .Decode(input, dictionary, TestFilterProvider.Instance, 0));
                new CcittFaxDecodeFilter(true).DecodeWithConsumption(input, dictionary,
                    TestFilterProvider.Instance, 0, out _, out var status);
                Assert.Equal(CcittFax.CcittFaxDecodeStatus.NotCompleted, status);
            }
        }

        /// <summary>A single Group 3 EOL is tolerated only after the exact intact declared rows.</summary>
        /// <remarks>This is a local EOF exception, not the six-EOL RTC defined by T.4. Mixed
        /// rows require tag 1 on the trailer. Height cannot authorize it;
        /// nonzero tails and missing/wrong tags remain failures. Both decoding paths agree.</remarks>
        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(2, true)]
        public void SingleGroup3EolRequiresExactRowsAndZeroFill(int k, bool endOfLine)
        {
            string tag = k > 0 ? "1" : "";
            string row = (endOfLine ? Eol : "") + tag + WhiteEight;
            foreach (int height in new[] { 0, 1, 2, 10 })
            foreach (bool lenient in new[] { false, true })
            foreach (int rows in new[] { 0, 1, 2, 3 })
            foreach (string tail in new[] { "", "00000000", new string('0', 1024), "1" })
            {
                var input = PackCcittBits(row + row + Eol + tag + tail);
                var dictionary = CreateFramedDictionary(k, rows, endOfLine, true, true)
                    .With(NameToken.Height, new NumericToken(height));
                bool accepted = rows == 2 && tail.IndexOf('1') < 0;
                if (!accepted && !lenient)
                {
                    Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                        .Decode(input, dictionary, TestFilterProvider.Instance, 0));
                    continue;
                }
                var pixels = new CcittFaxDecodeFilter(lenient).DecodeWithConsumption(input,
                    dictionary, TestFilterProvider.Instance, 0, out int consumed, out var status);
                Assert.Equal(accepted ? CcittFax.CcittFaxDecodeStatus.IncompleteEndOfBlock
                    : CcittFax.CcittFaxDecodeStatus.NotCompleted, status);
                if (accepted)
                {
                    Assert.Equal(new byte[height == 1 ? 1 : 2], pixels.ToArray());
                    Assert.Equal(input.Length, consumed);
                }
            }
        }

        /// <summary>A wrong mixed-format tag cannot become an EOF termination exception.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("0")]
        public void SingleGroup3EolRejectsMissingOrTwoDimensionalTag(string tag)
        {
            var dictionary = CreateFramedDictionary(2, 1, true, true, true);
            var input = PackCcittBits(Eol + "1" + WhiteEight + Eol + tag);
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                .Decode(input, dictionary, TestFilterProvider.Instance, 0));
        }

        /// <summary>Replacement rows never authorize the single-EOL declared-Rows tolerance.</summary>
        [Fact]
        public void SingleGroup3EolRejectsSubstitutedRows()
        {
            var input = PackCcittBits(Eol + WhiteEight + Eol + InvalidRun + Eol + WhiteEight + Eol + new string('0', 64));
            var dictionary = CreateFramedDictionary(0, 3, true, true, true, 1);
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                .Decode(input, dictionary, TestFilterProvider.Instance, 0));
        }

        /// <summary>Growth retains the previous row for 2D coding and stops at the full marker.</summary>
        /// <remarks>Start with one row, then cross several allocation boundaries. Rows and Height
        /// are smaller than the image; EndOfBlock makes the full RTC/EOFB authoritative. Compare
        /// pixels, byte consumption and status with signed decoding. Bytes after the marker are
        /// deliberately nonzero and must remain unread.</remarks>
        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(2, true)]
        [InlineData(-1, false)]
        public void CompactGrowthPreservesRowsAndMarkerConsumption(int k, bool endOfLine)
        {
            string row = k < 0 ? "1" : (endOfLine ? Eol : "") + (k > 0 ? "1" : "") + WhiteEight;
            string marker = k < 0 ? Eol + Eol : string.Concat(System.Linq.Enumerable.Repeat(Eol + (k > 0 ? "1" : ""), 6));
            string image = k < 0 ? "001" + BlackEight + string.Concat(System.Linq.Enumerable.Repeat("11", 16))
                : k > 0 ? Eol + "1" + BlackEight + string.Concat(System.Linq.Enumerable.Repeat(Eol + "0" + "11", 16))
                : string.Concat(System.Linq.Enumerable.Repeat(row, 17));
            var input = PackCcittBits(image + marker + "11111111");
            var type = k < 0 ? CcittFax.CcittFaxCompressionType.Group4_2D
                : k > 0 ? CcittFax.CcittFaxCompressionType.Group3_2D
                : endOfLine ? CcittFax.CcittFaxCompressionType.Group3_1D : CcittFax.CcittFaxCompressionType.ModifiedHuffman;
            foreach (bool blackIsOne in new[] { false, true })
            {
                var bitmap = new byte[1];
                Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(input, ref bitmap,
                    8, 1, type, false, blackIsOne, out int rows, out int consumed, out var status,
                    out long allocated, true, true, endOfLine, verifyEndMarker: true,
                    allowEndTolerance: true, expectedRowsForEndTolerance: 1, maximumGrowthBufferBytes: 1024));
                var expected = CcittFax.CcittFaxCompactDecoder.DecodeRowsToMemory(input, 8, type,
                    false, blackIsOne, false, true, true, 0, out int signedConsumed, out var signedStatus,
                    requireEndOfLine: endOfLine, requireEndMarker: true);
                Assert.Equal(17, rows);
                Assert.Equal(expected.ToArray(), bitmap.AsSpan(0, rows).ToArray());
                Assert.Equal(signedConsumed, consumed);
                Assert.True(consumed < input.Length);
                Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfBlock, status);
                Assert.Equal(signedStatus, status);
                Assert.InRange(allocated, 1L, 1024L);
                Assert.True(bitmap.Length > 1);
            }
        }

        /// <summary>Discarded image rows still update 2D references and establish the real block boundary.</summary>
        /// <remarks>The first black row is stored. Sixteen additional black rows must be decoded,
        /// including vertical operations referring to discarded rows, before recognizing RTC/EOFB.
        /// One scratch row replaces output growth. Following sentinel bytes belong to the next object
        /// and must not enter BytesConsumed. No-height callers continue to receive all rows.</remarks>
        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(2, true)]
        [InlineData(-1, false)]
        public void ImageOutputLimitPreservesReferencesAndBlockConsumption(int k, bool endOfLine)
        {
            var type = k < 0 ? CcittFax.CcittFaxCompressionType.Group4_2D
                : k > 0 ? CcittFax.CcittFaxCompressionType.Group3_2D
                : endOfLine ? CcittFax.CcittFaxCompressionType.Group3_1D : CcittFax.CcittFaxCompressionType.ModifiedHuffman;
            foreach (bool aligned in new[] { false, true })
            foreach (bool blackIsOne in new[] { false, true })
            {
                string first = k < 0 ? "001" + BlackEight : (endOfLine ? Eol : "") + (k > 0 ? "1" : "") + BlackEight;
                string next = k < 0 ? "11" : k > 0 ? Eol + "0" + "11" : first;
                if (aligned)
                {
                    first = first.PadRight((first.Length + 7) / 8 * 8, '0');
                    next = next.PadRight((next.Length + 7) / 8 * 8, '0');
                }
                string marker = k < 0 ? Eol + Eol : string.Concat(Enumerable.Repeat(Eol + (k > 0 ? "1" : ""), 6));
                var encoded = PackCcittBits(first + string.Concat(Enumerable.Repeat(next, 16)) + marker);
                var input = encoded.Concat(new byte[] { 0x45, 0x49, 0x20 }).ToArray();
                byte black = blackIsOne ? (byte)255 : (byte)0;
                var bitmap = new byte[1];
                Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(input, ref bitmap,
                    8, 1, type, aligned, blackIsOne, out int rows, out int consumed, out var status,
                    out long allocated, true, true, endOfLine, verifyEndMarker: true,
                    maximumGrowthBufferBytes: 42, maximumStoredRows: 1));
                Assert.Equal(17, rows);
                Assert.Equal(new[] { black }, bitmap);
                Assert.Equal(42, allocated); // One stored byte, two transition arrays and one scratch byte.
                Assert.Equal(encoded.Length, consumed);
                Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfBlock, status);
                var signed = CcittFax.CcittFaxCompactDecoder.DecodeRowsToMemory(input, 8, type,
                    aligned, blackIsOne, false, true, true, 0, out consumed, out status,
                    requireEndOfLine: endOfLine, requireEndMarker: true, maximumStoredRows: 1);
                Assert.Equal(new[] { black }, signed.ToArray());
                Assert.Equal(encoded.Length, consumed);
                Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfBlock, status);
                foreach (bool lenient in new[] { false, true })
                {
                    var dictionary = CreateFramedDictionary(k, 1, endOfLine, true, blackIsOne);
                    var parms = (DictionaryToken)dictionary.Data[NameToken.DecodeParms];
                    dictionary = dictionary.With(NameToken.DecodeParms, parms.With(NameToken.EncodedByteAlign,
                        aligned ? BooleanToken.True : BooleanToken.False));
                    var output = new CcittFaxDecodeFilter(lenient).DecodeWithConsumption(input,
                        dictionary, TestFilterProvider.Instance, 0, out consumed, out status);
                    Assert.Equal(new[] { black }, output.ToArray());
                    Assert.Equal(encoded.Length, consumed);
                    Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfBlock, status);
                }
            }
        }

        /// <summary>A positive Rows limit still ends decoding even when fewer image rows are stored.</summary>
        [Fact]
        public void PositiveRowsWithoutEndOfBlockStopsAfterDiscardedRows()
        {
            var input = PackCcittBits("111" + Eol + Eol);
            var dictionary = CreateFramedDictionary(-1, 3, false, false, true)
                .With(NameToken.H, new NumericToken(1)).Without(NameToken.Height);
            var pixels = new CcittFaxDecodeFilter(false).DecodeWithConsumption(input,
                dictionary, TestFilterProvider.Instance, 0, out int consumed, out var status);
            Assert.Equal(new byte[1], pixels.ToArray());
            Assert.Equal(1, consumed); // Three row bits; the following marker is not consumed.
            Assert.Equal(CcittFax.CcittFaxDecodeStatus.RowLimit, status);
        }

        /// <summary>Image cropping never authorizes corrupt data after the last stored row.</summary>
        [Fact]
        public void ImageOutputLimitDoesNotHideAnInvalidExtraRow()
        {
            var input = PackCcittBits("1" + "0000001"); // Complete white G4 row, then unsupported extension mode.
            var dictionary = CreateFramedDictionary(-1, 1, false, true, true);
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                .Decode(input, dictionary, TestFilterProvider.Instance, 0));
            var prefix = new CcittFaxDecodeFilter(true).DecodeWithConsumption(input, dictionary,
                TestFilterProvider.Instance, 0, out _, out var status);
            Assert.Equal(new byte[1], prefix.ToArray());
            Assert.Equal(CcittFax.CcittFaxDecodeStatus.NotCompleted, status);
        }

        /// <summary>Discarding rows saves storage but must not allow unlimited decoding work.</summary>
        [Fact]
        public void DiscardedRowsRetainAllocationAndLogicalOutputBudgets()
        {
            var marker = Eol + Eol;
            var bitmap = new byte[1];
            Assert.Throws<CorruptCompressedDataException>(() => CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(
                PackCcittBits("11" + marker), ref bitmap, 8, 1, CcittFax.CcittFaxCompressionType.Group4_2D,
                false, true, out _, out _, out _, out _, true, true, false,
                maximumGrowthBufferBytes: 41, maximumStoredRows: 1));
            Assert.Throws<CorruptCompressedDataException>(() => CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(
                PackCcittBits(new string('1', 43) + marker), ref bitmap, 8, 1, CcittFax.CcittFaxCompressionType.Group4_2D,
                false, true, out _, out _, out _, out _, true, true, false,
                maximumGrowthBufferBytes: 42, maximumStoredRows: 1));
            Assert.Throws<CorruptCompressedDataException>(() => CcittFax.CcittFaxCompactDecoder.DecodeRowsToMemory(
                PackCcittBits(new string('1', 100) + marker), 8, CcittFax.CcittFaxCompressionType.Group4_2D,
                false, true, false, true, true, 0, out _, out _, maximumDecodeBufferBytes: 90,
                requireEndOfLine: false, maximumStoredRows: 1));
        }

        /// <summary>Wide rows use signed decoding with the same image-output and consumption contract.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WideRowsRespectImageHeightWithoutStoppingAtIt(bool blackIsOne)
        {
            var encoded = PackCcittBits("11" + Eol + Eol);
            var input = encoded.Concat(new byte[] { 0x45, 0x49 }).ToArray();
            var dictionary = CreateFramedDictionary(-1, 2, false, true, blackIsOne)
                .With(NameToken.Height, new NumericToken(1));
            var parms = (DictionaryToken)dictionary.Data[NameToken.DecodeParms];
            dictionary = dictionary.With(NameToken.DecodeParms, parms.With(NameToken.Columns, new NumericToken(65536)));
            var output = new CcittFaxDecodeFilter(false).DecodeWithConsumption(input,
                dictionary, TestFilterProvider.Instance, 0, out int consumed, out var status);
            Assert.Equal(Enumerable.Repeat(blackIsOne ? (byte)0 : (byte)255, 8192), output.ToArray());
            Assert.Equal(encoded.Length, consumed);
            Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfBlock, status);
        }

        /// <summary>Unknown Rows uses Height for compact capacity, never for termination.</summary>
        /// <remarks>PDFBox 3.0.8 replaces unknown Rows with Height and fills a fixed bitmap.
        /// PdfPig keeps Rows unknown: a small Height caps storage, a large Height adds no white rows, and
        /// clean EOF exactly at capacity needs no growth. Black reference rows verify that 2D
        /// state survives resizing in unrestricted decoding and discarding in image decoding. Direct compact assertions prove success without a hidden
        /// signed retry; filter assertions cover absent and explicit-zero Rows in both modes.</remarks>
        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(2, true)]
        [InlineData(-1, false)]
        public void UnknownRowsWithEndOfBlockDisabledLimitsImageOutputButChecksAllRows(int k, bool endOfLine)
        {
            var type = k < 0 ? CcittFax.CcittFaxCompressionType.Group4_2D
                : k > 0 ? CcittFax.CcittFaxCompressionType.Group3_2D
                : endOfLine ? CcittFax.CcittFaxCompressionType.Group3_1D : CcittFax.CcittFaxCompressionType.ModifiedHuffman;
            foreach (bool aligned in new[] { false, true })
            foreach (bool blackIsOne in new[] { false, true })
            {
                string firstRow = k < 0 ? "001" + BlackEight : (endOfLine ? Eol : "") + (k > 0 ? "1" : "") + BlackEight;
                string nextRow = k < 0 ? "11" : k > 0 ? Eol + "0" + "11" : firstRow;
                if (aligned)
                {
                    firstRow = firstRow.PadRight((firstRow.Length + 7) / 8 * 8, '0');
                    nextRow = nextRow.PadRight((nextRow.Length + 7) / 8 * 8, '0');
                }
                var input = PackCcittBits(firstRow + string.Concat(Enumerable.Repeat(nextRow, 16)));
                var expected = Enumerable.Repeat(blackIsOne ? (byte)255 : (byte)0, 17).ToArray();
                foreach (int height in new[] { 1, 17, 25 })
                {
                    var bitmap = new byte[height];
                    Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(input, ref bitmap,
                        8, height, type, aligned, blackIsOne, out int rows, out int consumed, out var status,
                        out long allocated, true, false, endOfLine, allowEndOfInput: true,
                        maximumGrowthBufferBytes: 1024));
                    Assert.Equal(17, rows);
                    Assert.Equal(expected, bitmap.AsSpan(0, rows).ToArray());
                    Assert.Equal(input.Length, consumed);
                    Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfInput, status);
                    Assert.Equal(height == 17 ? 57L : height == 25 ? 65L : 103L, allocated);
                    var dictionary = CreateFramedDictionary(k, 0, endOfLine, false, blackIsOne)
                        .With(NameToken.Height, new NumericToken(height));
                    var parms = (DictionaryToken)dictionary.Data[NameToken.DecodeParms];
                    dictionary = dictionary.With(NameToken.DecodeParms, parms.With(NameToken.EncodedByteAlign,
                        aligned ? BooleanToken.True : BooleanToken.False));
                    foreach (bool omitRows in new[] { false, true })
                    {
                        var parameters = ((DictionaryToken)dictionary.Data[NameToken.DecodeParms]).Data
                            .Where(x => !omitRows || x.Key != NameToken.Rows.Data).ToDictionary(x => NameToken.Create(x.Key), x => x.Value);
                        var framed = dictionary.With(NameToken.DecodeParms, new DictionaryToken(parameters));
                        foreach (bool lenient in new[] { false, true })
                        {
                            var output = new CcittFaxDecodeFilter(lenient).DecodeWithConsumption(input,
                                framed, TestFilterProvider.Instance, 0, out int filterConsumed, out var filterStatus);
                            Assert.Equal(expected.Take(Math.Min(height, expected.Length)).ToArray(), output.ToArray());
                            Assert.Equal(consumed, filterConsumed);
                            Assert.Equal(status, filterStatus);
                        }
                    }
                }
            }
        }

        /// <summary>Unknown-Rows growth still enforces the allocation ceiling before resizing.</summary>
        [Fact]
        public void UnknownRowsWithoutEndMarkerChecksEofBeforeBudgetedGrowth()
        {
            var bitmap = new byte[1];
            Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(PackCcittBits(WhiteEight),
                ref bitmap, 8, 1, CcittFax.CcittFaxCompressionType.ModifiedHuffman, false, true,
                out _, out _, out _, out long allocated, true, false, false, allowEndOfInput: true,
                maximumGrowthBufferBytes: 41));
            Assert.Equal(41, allocated);
            Assert.Single(bitmap);
            Assert.Throws<CorruptCompressedDataException>(() => CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(
                PackCcittBits(WhiteEight + WhiteEight), ref bitmap, 8, 1,
                CcittFax.CcittFaxCompressionType.ModifiedHuffman, false, true,
                out _, out _, out _, out _, true, false, false, allowEndOfInput: true,
                maximumGrowthBufferBytes: 41));
            Assert.Single(bitmap);
        }

        /// <summary>The allocation ceiling applies before growth, including old arrays.</summary>
        [Fact]
        public void CompactGrowthEnforcesBudgetAndDoesNotAllocateAtFinalMarker()
        {
            // Width 8 needs 40 bytes for two UInt16 transition arrays plus a one-byte bitmap.
            var bitmap = new byte[1];
            var final = PackCcittBits(WhiteEight + string.Concat(System.Linq.Enumerable.Repeat(Eol, 6)));
            Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(final, ref bitmap,
                8, 1, CcittFax.CcittFaxCompressionType.ModifiedHuffman, false, true,
                out _, out _, out _, out long allocated, true, true, false,
                verifyEndMarker: true, maximumGrowthBufferBytes: 41));
            Assert.Equal(41, allocated);
            Assert.Single(bitmap);
            var more = PackCcittBits(WhiteEight + WhiteEight + string.Concat(System.Linq.Enumerable.Repeat(Eol, 6)));
            Assert.Throws<CorruptCompressedDataException>(() => CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(
                more, ref bitmap, 8, 1, CcittFax.CcittFaxCompressionType.ModifiedHuffman, false, true,
                out _, out _, out _, out _, true, true, false, verifyEndMarker: true,
                maximumGrowthBufferBytes: 41));
            Assert.Single(bitmap);
        }

        [Theory]
        [InlineData(0, false, "10011")]
        [InlineData(2, true, "000000000001110011")]
        /// <remarks>
        /// Directly assert compact success at clean unknown-Rows EOF, both at bitmap capacity and before it.
        /// The filter must return only the two real rows.
        /// </remarks>
        [InlineData(-1, false, "1")]
        public void CompactUnknownRowsRecognizesCleanEofWithoutFallback(int k, bool endOfLine, string row)
        {
            var input = PackCcittBits(row + row);
            var type = k < 0 ? CcittFax.CcittFaxCompressionType.Group4_2D
                : k > 0 ? CcittFax.CcittFaxCompressionType.Group3_2D
                : CcittFax.CcittFaxCompressionType.ModifiedHuffman;
            foreach (int height in new[] { 2, 10 })
            {
                Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(input, new byte[height],
                    8, height, type, false, true, out int rows, out int consumed, out var status,
                    true, true, endOfLine, verifyEndMarker: true, allowEndOfInput: true));
                Assert.Equal(2, rows);
                Assert.Equal(input.Length, consumed);
                Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfInput, status);
                var dictionary = CreateFramedDictionary(k, 0, endOfLine, true, true)
                    .With(NameToken.Height, new NumericToken(height));
                Assert.Equal(new byte[2], new CcittFaxDecodeFilter(false).Decode(input,
                    dictionary, TestFilterProvider.Instance, 0).ToArray());
            }
        }

        /// <remarks>
        /// A repaired row occupies normal output bytes but is not an intact decoded row. Even equal output
        /// length cannot authorize a declared-Rows short RTC.
        /// </remarks>
        [Fact]
        public void SubstitutedRowsDoNotQualifyForDeclaredRowsIncompleteRtc()
        {
            var input = PackCcittBits(Eol + WhiteEight + Eol + InvalidRun
                + Eol + WhiteEight + Eol + Eol + Eol);
            var dictionary = CreateFramedDictionary(0, 3, true, true, true, 1);
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                .Decode(input, dictionary, TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData("00000000")]
        /// <remarks>
        /// With unknown Rows, clean EOF allows only final-byte zero padding. An extra whole zero byte
        /// or nonzero remaining bit must fail compact termination; Height alone cannot authorize fill.
        /// </remarks>
        [InlineData("1")]
        public void CompactCleanEofDoesNotHideExtraBytesOrNonzeroBits(string tail)
        {
            var input = PackCcittBits(WhiteEight + WhiteEight + tail);
            Assert.False(CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(input, new byte[2],
                8, 2, CcittFax.CcittFaxCompressionType.ModifiedHuffman, false, true,
                out _, out _, out _, true, true, false, verifyEndMarker: true, allowEndOfInput: true));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(2, true)]
        /// <remarks>
        /// Exactly two complete declared rows followed only by final-byte padding are accepted in both
        /// parsing modes. Missing markers remain distinguishable as EndOfInput, with all input bytes
        /// consumed.
        /// </remarks>
        [InlineData(-1, false)]
        public void DeclaredCompleteRowsAcceptCleanEofInBothPaths(int k, bool endOfLine)
        {
            string row = k < 0 ? "1" : (endOfLine ? Eol : "") + (k > 0 ? "1" : "") + WhiteEight;
            var input = PackCcittBits(row + row);
            foreach (bool lenient in new[] { false, true })
            foreach (int height in new[] { 0, 1, 2, 10 })
            {
                var dictionary = CreateFramedDictionary(k, 2, endOfLine, true, true)
                    .With(NameToken.Height, new NumericToken(height));
                var pixels = new CcittFaxDecodeFilter(lenient).DecodeWithConsumption(input,
                    dictionary, TestFilterProvider.Instance, 0, out int consumed, out var status);
                Assert.Equal(new byte[height == 1 ? 1 : 2], pixels.ToArray());
                Assert.Equal(input.Length, consumed);
                Assert.Equal(CcittFax.CcittFaxDecodeStatus.EndOfInput, status);
            }
        }

        /// <summary>Exact intact Rows authorize zero-filled EOF, with or without a single EOL.</summary>
        /// <remarks>Whole zero bytes are a deliberate local compatibility tolerance, not merely
        /// byte alignment and not a full RTC/EOFB. Such bits could also be an incomplete extra
        /// code; exact Rows are the policy's evidence for retaining this declared image. Vary
        /// coding family, polarity, alignment, Height and parsing mode. Check direct compact
        /// success, signed output, status and consumption through the entire fill.</remarks>
        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(2, true)]
        [InlineData(-1, false)]
        public void DeclaredRowsAcceptZeroFillThroughEof(int k, bool endOfLine)
        {
            var type = k < 0 ? CcittFax.CcittFaxCompressionType.Group4_2D
                : k > 0 ? CcittFax.CcittFaxCompressionType.Group3_2D
                : endOfLine ? CcittFax.CcittFaxCompressionType.Group3_1D : CcittFax.CcittFaxCompressionType.ModifiedHuffman;
            foreach (bool aligned in new[] { false, true })
            foreach (bool blackIsOne in new[] { false, true })
            foreach (bool withEol in new[] { false, true })
            foreach (int fillBits in new[] { 8, 65, 4096 })
            {
                string row = k < 0 ? "1" : (endOfLine ? Eol : "") + (k > 0 ? "1" : "") + WhiteEight;
                if (aligned) row += new string('0', (8 - row.Length % 8) % 8);
                string trailer = withEol ? Eol + (k > 0 ? "1" : "") : "";
                var input = PackCcittBits(row + row + trailer + new string('0', fillBits));
                var expected = new[] { blackIsOne ? (byte)0 : (byte)255, blackIsOne ? (byte)0 : (byte)255 };
                var expectedStatus = withEol ? CcittFax.CcittFaxDecodeStatus.IncompleteEndOfBlock : CcittFax.CcittFaxDecodeStatus.EndOfInput;
                var bitmap = new byte[2];
                Assert.True(CcittFax.CcittFaxCompactDecoder.TryDecodeBitmap(input, bitmap, 8, 2, type,
                    aligned, blackIsOne, out int rows, out int consumed, out var status,
                    true, true, endOfLine, verifyEndMarker: true, allowEndTolerance: true, expectedRowsForEndTolerance: 2));
                Assert.Equal(2, rows);
                Assert.Equal(expected, bitmap);
                Assert.Equal(input.Length, consumed);
                Assert.Equal(expectedStatus, status);
                var signed = CcittFax.CcittFaxCompactDecoder.DecodeRowsToMemory(input, 8, type,
                    aligned, blackIsOne, false, true, true, 0, out consumed, out status,
                    requireEndOfLine: endOfLine, requireEndMarker: true, allowEndTolerance: true, expectedRowsForEndTolerance: 2);
                Assert.Equal(expected, signed.ToArray());
                Assert.Equal(input.Length, consumed);
                Assert.Equal(expectedStatus, status);
                foreach (bool lenient in new[] { false, true })
                foreach (int height in new[] { 0, 1, 2, 10 })
                {
                    var dictionary = CreateFramedDictionary(k, 2, endOfLine, true, blackIsOne).With(NameToken.Height, new NumericToken(height));
                    var parameters = (DictionaryToken)dictionary.Data[NameToken.DecodeParms];
                    dictionary = dictionary.With(NameToken.DecodeParms, parameters.With(NameToken.EncodedByteAlign, aligned ? BooleanToken.True : BooleanToken.False));
                    var result = new CcittFaxDecodeFilter(lenient).DecodeWithConsumption(input, dictionary,
                        TestFilterProvider.Instance, 0, out consumed, out status);
                    Assert.Equal(expected.Take(height > 0 ? height : expected.Length).ToArray(), result.ToArray());
                    Assert.Equal(input.Length, consumed);
                    Assert.Equal(expectedStatus, status);
                }
            }
        }

        /// <summary>A failed zero-fill probe never consumes possible image data.</summary>
        /// <remarks>Place a set bit at the start, middle and end of a long tail. Rejection
        /// must preserve BytesConsumed and the next code prefix, including across refills.</remarks>
        [Theory]
        [InlineData(0)]
        [InlineData(511)]
        [InlineData(1023)]
        public void ZeroFillProbePreservesPositionOnNonzeroInput(int setBit)
        {
            string tail = new string('0', setBit) + "1" + new string('0', 1023 - setBit);
            var reader = new CcittFax.CcittFaxCompactBitReader(PackCcittBits("101" + tail));
            reader.ReadBitsExact(3);
            int consumed = reader.BytesConsumed;
            Assert.False(reader.TryReadZeroFillToEnd());
            Assert.Equal(consumed, reader.BytesConsumed);
            Assert.Equal(tail[0] == '1' ? 1 : 0, reader.ReadBitsExact(1));
        }

        /// <summary>Whole-byte fill does not gain legitimacy from Height or unknown Rows.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(-1)]
        public void UnknownRowsDoesNotAuthorizeWholeByteZeroFill(int k)
        {
            string row = k < 0 ? "1" : (k > 0 ? Eol + "1" : "") + WhiteEight;
            foreach (int height in new[] { 0, 1, 10 })
                Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                    .Decode(PackCcittBits(row + new string('0', 64)),
                        CreateFramedDictionary(k, 0, k > 0, true, true).With(NameToken.Height, new NumericToken(height)),
                        TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// Two intact Group 4 rows with Rows=2 authorize a single EOL followed exclusively by zero fill.
        /// Leading zero fill is allowed; the result is IncompleteEndOfBlock, not a full EOFB.
        /// </remarks>
        [InlineData(true)]
        public void DeclaredCompleteGroup4RowsAcceptOneEolWithZeroFill(bool blackIsOne)
        {
            foreach (bool lenient in new[] { false, true })
            foreach (int height in new[] { 0, 1, 2, 10 })
            foreach (int leadingZeroFill in new[] { 0, 5, 10, 16 })
            foreach (int trailingZeroFill in new[] { 0, 8, 65, 1024 })
            {
                var input = PackCcittBits("11" + new string('0', leadingZeroFill) + Eol + new string('0', trailingZeroFill));
                var dictionary = CreateFramedDictionary(-1, 2, false, true, blackIsOne)
                    .With(NameToken.Height, new NumericToken(height));
                var pixels = new CcittFaxDecodeFilter(lenient).DecodeWithConsumption(input,
                    dictionary, TestFilterProvider.Instance, 0, out int consumed, out var status);
                byte white = blackIsOne ? (byte)0 : (byte)255;
                Assert.Equal(Enumerable.Repeat(white, height == 1 ? 1 : 2).ToArray(), pixels.ToArray());
                Assert.Equal(input.Length, consumed);
                Assert.Equal(CcittFax.CcittFaxDecodeStatus.IncompleteEndOfBlock, status);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        /// <remarks>
        /// Reject missing or extra declared rows and nonzero trailers; exact Rows authorize zero fill.
        /// Skip genuinely valid endings instead of treating another complete row as corrupt padding.
        /// </remarks>
        [InlineData(-1)]
        public void EndToleranceDoesNotHideMismatchedRowsOrExtraData(int k)
        {
            string row = k < 0 ? "1" : (k > 0 ? Eol + "1" : "") + WhiteEight;
            foreach (int height in new[] { 0, 2, 10 })
            foreach (int rows in new[] { 1, 2, 3 })
            foreach (string trailer in k < 0
                ? new[] { "", Eol, "00000000", Eol + "00000000", Eol + "1", "00000000000" }
                : new[] { "", "00000000", Eol, Eol + "1" })
            {
                if (rows == 2 && (trailer.IndexOf('1') < 0 || trailer == Eol
                    || k > 0 && trailer == Eol + "1" || k < 0 && trailer == Eol + "00000000")) continue;
                // An optional Group 4 EOL followed by vertical-zero is a real third row.
                if (k < 0 && rows == 3 && trailer == Eol + "1") continue;
                var dictionary = CreateFramedDictionary(k, rows, k > 0, true, true)
                    .With(NameToken.Height, new NumericToken(height));
                Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                    .Decode(PackCcittBits(row + row + trailer), dictionary, TestFilterProvider.Instance, 0));
            }
        }

        /// <remarks>
        /// A positive Height cannot substitute for known Rows. Rows=0 must still reject a single-EOL Group 4
        /// trailer in strict parsing.
        /// </remarks>
        [Fact]
        public void UnknownRowsDoesNotEnableSingleEolGroup4Exception()
        {
            foreach (int height in new[] { 0, 1, 10 })
                Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                    .Decode(PackCcittBits("1" + Eol), CreateFramedDictionary(-1, 0, false, true, true)
                        .With(NameToken.Height, new NumericToken(height)), TestFilterProvider.Instance, 0));
        }

        /// <remarks>
        /// The declared-Rows clean-EOF exception requires every row to be intact, not merely an output
        /// buffer whose length matches Rows after substitution.
        /// </remarks>
        [Fact]
        public void ReplacedRowsCannotEnableCleanEofTolerance()
        {
            var input = PackCcittBits(Eol + WhiteEight + Eol + InvalidRun + Eol + WhiteEight + new string('0', 64));
            Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                .Decode(input, CreateFramedDictionary(0, 3, true, true, true, 1), TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData(0)]
        /// <remarks>
        /// Alignment would discard a set bit after the complete row. Validate EOF padding first so this
        /// invalid ending remains rejected in compact and growing paths.
        /// </remarks>
        [InlineData(-1)]
        public void ByteAlignmentCannotHideNonzeroEofPadding(int k)
        {
            string row = k < 0 ? "1" : WhiteEight;
            var dictionary = CreateFramedDictionary(k, 1, false, true, true);
            var parameters = (DictionaryToken)dictionary.Data[NameToken.DecodeParms];
            dictionary = dictionary.With(NameToken.DecodeParms, parameters.With(NameToken.EncodedByteAlign, BooleanToken.True));
            foreach (int height in new[] { 0, 1, 10 })
                Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                    .Decode(PackCcittBits(row + "1"), dictionary.With(NameToken.Height, new NumericToken(height)),
                        TestFilterProvider.Instance, 0));
        }

        [Theory]
        [InlineData(false)]
        /// <remarks>
        /// Contrast seven zero alignment bits with a set alignment bit before the EOL. Only the zero-filled
        /// version may authorize a shortened Group 4 EOFB.
        /// </remarks>
        [InlineData(true)]
        public void SingleEolExceptionValidatesPaddingBeforeByteAlignment(bool zeroPadding)
        {
            var dictionary = CreateFramedDictionary(-1, 1, false, true, true);
            var parameters = (DictionaryToken)dictionary.Data[NameToken.DecodeParms];
            dictionary = dictionary.With(NameToken.DecodeParms, parameters.With(NameToken.EncodedByteAlign, BooleanToken.True));
            var input = PackCcittBits("1" + (zeroPadding ? "0000000" : "1000000") + Eol);
            foreach (int height in new[] { 0, 1, 10 })
            {
                var framed = dictionary.With(NameToken.Height, new NumericToken(height));
                if (!zeroPadding)
                    Assert.Throws<CorruptCompressedDataException>(() => new CcittFaxDecodeFilter(false)
                        .Decode(input, framed, TestFilterProvider.Instance, 0));
                else
                    Assert.Equal(new byte[1], new CcittFaxDecodeFilter(false)
                        .Decode(input, framed, TestFilterProvider.Instance, 0).ToArray());
            }
        }

        /// <summary>Packs readable bit strings most-significant bit first into filter input.</summary>
        /// <remarks>The final byte is implicitly filled with zeros. Those added bits count in
        /// EOF tests; appending eight explicit zeros creates an extra byte, not ordinary final
        /// byte padding. Bits beyond a declared row may also encode another real row, so
        /// tests must interpret their codes rather than assume every trailer is invalid.</remarks>
        private static byte[] PackCcittBits(string bits)
        {
            var bytes = new byte[(bits.Length + 7) / 8];
            for (int i = 0; i < bits.Length; i++)
                if (bits[i] == '1')
                    bytes[i >> 3] |= (byte)(128 >> (i & 7));
            return bytes;
        }

        /// <summary>Creates parameters for eight-pixel rows with independent Rows and Height semantics.</summary>
        /// <remarks>Rows governs filter termination; Height initially matches it only to give
        /// tests a useful capacity hint. Individual tests override or omit Height to exercise
        /// both decoder paths without changing Rows, coding type, or expected pixels.</remarks>
        private static DictionaryToken CreateFramedDictionary(int k, int rows, bool endOfLine, bool endOfBlock,
            bool blackIsOne, int damagedRowsBeforeError = 0, bool includeImageHeight = true)
        {
            var parameters = new Dictionary<NameToken, IToken>
            {
                { NameToken.Columns, new NumericToken(8) }, { NameToken.Rows, new NumericToken(rows) },
                { NameToken.K, new NumericToken(k) }, { NameToken.EndOfLine, endOfLine ? BooleanToken.True : BooleanToken.False },
                { NameToken.BlackIs1, blackIsOne ? BooleanToken.True : BooleanToken.False },
                { NameToken.Create("EndOfBlock"), endOfBlock ? BooleanToken.True : BooleanToken.False },
                { NameToken.Create("DamagedRowsBeforeError"), new NumericToken(damagedRowsBeforeError) }
            };
            var dictionary = new Dictionary<NameToken, IToken>
            {
                { NameToken.Filter, NameToken.CcittfaxDecode }, { NameToken.DecodeParms, new DictionaryToken(parameters) }
            };
            if (includeImageHeight)
                dictionary.Add(NameToken.Height, new NumericToken(rows));
            return new DictionaryToken(dictionary);
        }

        private static DictionaryToken CreateSmallImageDictionary(int k, bool? endOfLine = null)
        {
            var parameters = new Dictionary<NameToken, IToken>
            {
                { NameToken.Columns, new NumericToken(8) },
                { NameToken.Rows, new NumericToken(1) },
                { NameToken.Create("EndOfBlock"), BooleanToken.False },
                { NameToken.K, new NumericToken(k) },
                { NameToken.BlackIs1, BooleanToken.True }
            };
            if (endOfLine.HasValue)
            {
                parameters.Add(NameToken.EndOfLine, endOfLine.Value ? BooleanToken.True : BooleanToken.False);
            }
            return new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Filter, NameToken.CcittfaxDecode },
                { NameToken.Height, new NumericToken(1) },
                { NameToken.DecodeParms, new DictionaryToken(parameters) }
            });
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(31)]
        [InlineData(32)]
        [InlineData(33)]
        [InlineData(1800)]
        public void DirectPolarityPreservesInputSliceAndWhitePadding(int columns)
        {
            // Four vertical-zero codes encode four white Group 4 rows. The surrounding bytes
            // are deliberately outside the supplied input slice and must not be consumed or changed.
            var input = new byte[] { 0xAA, 0xF0, 0x55 };
            foreach (bool blackIsOne in new[] { true, false })
            {
                var parameters = new DictionaryToken(new Dictionary<NameToken, IToken> {
                    { NameToken.K, new NumericToken(-1) }, { NameToken.Columns, new NumericToken(columns) },
                    { NameToken.Rows, new NumericToken(4) }, { NameToken.BlackIs1, blackIsOne ? BooleanToken.True : BooleanToken.False } });
                var dictionary = new DictionaryToken(new Dictionary<NameToken, IToken> {
                    { NameToken.Filter, NameToken.CcittfaxDecode }, { NameToken.DecodeParms, parameters } });
                var actual = new CcittFaxDecodeFilter().Decode(input.AsMemory(1, 1), dictionary, DefaultFilterProvider.Instance, 0);
                var expected = Enumerable.Repeat(blackIsOne ? (byte)0 : (byte)255, (columns + 7) / 8 * 4).ToArray();
                Assert.Equal(expected, actual.ToArray());
                Assert.Equal(new byte[] { 0xAA, 0xF0, 0x55 }, input);
            }
        }

        private static byte[] CreateAllocationBombPdf(int columns, int rows, bool filterChain)
        {
            const string content = "q 1 0 0 1 0 0 cm /Bomb Do Q\n";
            var data = filterChain ? "00>" : "\0";
            var filters = filterChain ? "[/ASCIIHexDecode /CCITTFaxDecode]" : "/CCITTFaxDecode";
            var parameters = $"<< /K -1 /Columns {columns} /Rows {rows} >>";
            if (filterChain) parameters = $"[null {parameters}]";
            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << /Bomb 5 0 R >> >> /Contents 4 0 R >>",
                $"<< /Length {content.Length} >>\nstream\n{content}endstream",
                $"<< /Type /XObject /Subtype /Image /Width {columns} /Height {rows} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter {filters} /DecodeParms {parameters} /Length {data.Length} >>\nstream\n{data}\nendstream"
            };
            var builder = new StringBuilder("%PDF-1.7\n");
            var offsets = new List<int>();
            for (var i = 0; i < objects.Length; i++)
            {
                offsets.Add(builder.Length);
                builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            var xrefOffset = builder.Length;
            builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                builder.Append($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
            }
            builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CanDecodeCCittFaxCompressedImageData(bool lenient)
        {
            var encodedBytes = ImageHelpers.LoadFileBytes("ccittfax-encoded.bin");

            var filter = new CcittFaxDecodeFilter(lenient);
            var dictionary = new Dictionary<NameToken, IToken>
            {
                { NameToken.D, new ArrayToken(new []{ new NumericToken(1), new NumericToken(0) })},
                { NameToken.W, new NumericToken(1800) },
                { NameToken.H, new NumericToken(3113) },
                { NameToken.Bpc, new NumericToken(1) },
                { NameToken.F, NameToken.CcittfaxDecode },
                { NameToken.DecodeParms,
                    new DictionaryToken(new Dictionary<NameToken, IToken>
                    {
                        { NameToken.K, new NumericToken(-1) },
                        { NameToken.Columns, new NumericToken(1800) },
                        { NameToken.Rows, new NumericToken(3113) },
                        { NameToken.BlackIs1, BooleanToken.True }
                    })
                }
            };

            var expectedBytes = ImageHelpers.LoadFileBytes("ccittfax-decoded.bin");
            var decodedBytes = filter.Decode(encodedBytes, new DictionaryToken(dictionary), TestFilterProvider.Instance, 0);
            Assert.Equal(expectedBytes, decodedBytes.ToArray());
        }
    }
}
