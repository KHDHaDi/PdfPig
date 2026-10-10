namespace UglyToad.PdfPig.Filters
{
    using System;
    using CcittFax;
    using Core;
    using Fonts;
    using Tokens;
    using Util;

    /// <summary>Resolves PDF CCITTFaxDecode parameters and decodes image data into a bounded bitmap.</summary>
    /// <remarks>
    /// <para>Validate Columns, Rows and the image Height capacity hint before allocating. K selects Group 4,
    /// mixed Group 3, or 1D coding; for K = 0, explicit EndOfLine or bounded header inspection selects
    /// EOL-synchronized Group 3 versus Modified Huffman with optional EOL. EncodedByteAlign,
    /// BlackIs1, EndOfBlock and the applicable DamagedRowsBeforeError budget are passed to
    /// <see cref="CcittFaxCompactDecoder"/>. Rows = 0 selects bounded decoding until input exhaustion or an end marker.
    /// EndOfBlock = true overrides Rows; positive image Height bounds stored pixels independently of filter termination.</para>
    /// <para>The 256 MiB per-call buffer estimate includes the bitmap, one row and two Int32 transition
    /// arrays. It excludes object headers and static lookup tables and is not a document-wide or
    /// process-memory limit. Unknown-height growth additionally budgets old and new output arrays
    /// during resizing. Strict parsing rejects nonpositive Columns, negative Rows and
    /// empty input; lenient parsing can return empty output. Neither mode bypasses the limit.</para>
    /// <para>Row framing and image termination are different decisions. EOL (end of line) is
    /// the bit pattern 000000000001: eleven zero bits and one set bit, possibly preceded by
    /// additional zero fill. An EOL can introduce another image row; it does not by itself
    /// terminate the image. Group 3 RTC (return to control) consists of six EOLs. In mixed
    /// Group 3, each RTC EOL also has a one-dimensional tag bit of 1. Group 4 EOFB (end of
    /// facsimile block) consists of two EOLs. EOF means exhaustion of the filter input, not
    /// the presence of either encoded marker.</para>
    /// <para>Termination policy, applied only between completely decoded rows:</para>
    /// <list type="table">
    /// <listheader><term>Ending</term><description>Acceptance and reported status</description></listheader>
    /// <item><term>Full RTC or EOFB</term><description>With EndOfBlock enabled, stop at the
    /// complete marker even when Rows differs. Report EndOfBlock. Later input bytes may
    /// belong to enclosing PDF syntax; they are not part of this decoded image.</description></item>
    /// <item><term>EndOfBlock disabled</term><description>Stop at positive Rows or clean EOF,
    /// whichever comes first. Rows = 0 has no row limit. Report RowLimit or EndOfInput.</description></item>
    /// <item><term>EOF without an end marker</term><description>Unknown Rows permits only zero
    /// to seven final-byte zero bits after a complete row. With EndOfBlock enabled and exactly
    /// positive Rows decoded without substitution, accept exclusively zero fill through EOF,
    /// including whole zero bytes. Report EndOfInput and consume the fill.</description></item>
    /// <item><term>Short Group 3 RTC</term><description>With EndOfBlock enabled, accept two to
    /// five complete EOLs, with the required mixed-format tags, only after at least one row.
    /// Rows must be unknown or exactly matched without damaged-row substitution. All remaining
    /// input must be zero fill through EOF, including whole zero-fill bytes. Unlike the
    /// single-EOL exception, unknown Rows may also authorize this established rule. Report IncompleteEndOfBlock.</description></item>
    /// <item><term>Single Group 3 EOL</term><description>With EndOfBlock enabled, accept one
    /// EOL (with tag 1 in mixed format) only after exactly positive Rows, without substituted
    /// rows. Only zero fill may follow through EOF, including whole zero bytes. Height and unknown Rows
    /// cannot authorize it. This local exception reports IncompleteEndOfBlock.</description></item>
    /// <item><term>Short Group 4 EOFB</term><description>With EndOfBlock enabled, accept one
    /// complete EOL, possibly preceded by zero fill, only after exactly the positive Rows
    /// count with no damaged-row substitution. Only zero fill may follow through
    /// EOF, including whole zero bytes. Unknown Rows and image Height cannot authorize this exception. Report
    /// IncompleteEndOfBlock.</description></item>
    /// <item><term>Invalid or incomplete row</term><description>The end checks cannot repair
    /// it. Strict parsing throws. Lenient parsing may return earlier complete rows with
    /// NotCompleted; that status does not establish a successfully decoded image boundary.
    /// Only the separate explicit damaged-row budget permits replacement.</description></item>
    /// </list>
    /// <para>The shortened-marker and declared-Rows EOF rules are local PdfPig compatibility
    /// decisions motivated by real files with intact image data and missing termination bits.
    /// Both parsing modes use these narrowly checked rules; strict does not mean that every
    /// accepted trailer is a full standard marker. The exceptions never manufacture rows,
    /// infer Rows from Height, or bypass the allocation limit. Byte alignment must not discard
    /// nonzero bits before the EOF/single-EOL checks have validated them.</para>
    /// <para>Zero-fill tolerance is a local compatibility decision, not a full standard block
    /// marker. An all-zero tail can be intended fill or the truncated beginning of an additional
    /// row; those cannot be distinguished from bits alone. After exact intact positive Rows we
    /// retain the declared image. Nonzero tails are not discarded: decode further complete rows
    /// or apply the normal error policy. Unknown Rows and Height alone never enable this rule.
    /// Tail checking is linear in the remaining input, creates no output and preserves the
    /// reader position on rejection. Accepted fill is included in BytesConsumed.</para>
    /// <para>Implementation provenance:</para>
    /// <list type="table">
    /// <listheader><term>Source</term><description>Reused or added behavior</description></listheader>
    /// <item><term>Original Apache PDFBox port (Apache-2.0)</term><description>PDF parameter resolution
    /// and CCITT filter structure. Original attribution:
    /// <see href="https://github.com/apache/pdfbox/blob/714156a15ea6fcfe44ac09345b01e192cbd74450/pdfbox/src/main/java/org/apache/pdfbox/filter/CCITTFaxFilter.java">CCITTFaxFilter</see>.</description></item>
    /// <item><term>PDFBox 3.0.8 (Apache-2.0)</term><description>Image-height capacity hints, positive-dimension
    /// validation, explicit EndOfLine handling and bounded EOL header detection.
    /// See <see href="https://github.com/apache/pdfbox/blob/3.0.8/pdfbox/src/main/java/org/apache/pdfbox/filter/CCITTFaxFilter.java">release filter</see>.
    /// PdfPig applies its own lenient policy to invalid dimensions.</description></item>
    /// <item><term>Adobe PDF Reference 1.7, section 3.3.5</term><description>Optional EOL,
    /// EOFB/RTC termination, unknown Rows and damaged-row substitution rules.
    /// <see href="https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.7old.pdf">PDF Reference</see>.
    /// Framing, resynchronization and bounded growth are implemented in C# here. Filter termination uses Rows/EndOfBlock
    /// independently of the image Height, and returns only complete image rows up to that height.
    /// </description></item>
    /// <item><term>PdfPig (Apache-2.0)</term><description>Int64 buffer-size arithmetic, the per-call
    /// allocation ceiling, document-scoped strict/lenient configuration and compact bitmap decoding.</description></item>
    /// </list>
    /// </remarks>
    public sealed class CcittFaxDecodeFilter : IFilter
    {
        internal const long MaximumDecodeBufferBytes = 256L * 1024 * 1024;

        internal bool UseLenientParsing { get; }

        /// <summary>Creates a lenient CCITT filter. The allocation budget remains enforced.</summary>
        public CcittFaxDecodeFilter() : this(useLenientParsing: true)
        {
        }

        internal CcittFaxDecodeFilter(bool useLenientParsing)
        {
            UseLenientParsing = useLenientParsing;
        }

        /// <inheritdoc />
        public bool IsSupported { get; } = true;

        /// <inheritdoc />
        public Memory<byte> Decode(Memory<byte> input,
            DictionaryToken streamDictionary,
            IFilterProvider filterProvider,
            int filterIndex)
            => DecodeWithConsumption(input, streamDictionary, filterProvider, filterIndex, out _, out _);

        /// <summary>Decodes pixels and reports consumption in this filter stage's input, rounded to a byte boundary.</summary>
        /// <remarks>Only a complete block marker establishes an explicit encoded end. Row limits and EOF
        /// have distinct statuses; NotCompleted is diagnostic only. The incomplete-end-of-block status identifies
        /// the compatibility exception. Offsets do not map through earlier filters to the original PDF.</remarks>
        internal Memory<byte> DecodeWithConsumption(Memory<byte> input, DictionaryToken streamDictionary,
            IFilterProvider filterProvider, int filterIndex, out int bytesConsumed, out CcittFaxDecodeStatus status)
        {
            bytesConsumed = 0;
            status = CcittFaxDecodeStatus.NotCompleted;
            var decodeParms = DecodeParameterResolver.GetFilterParameters(streamDictionary, filterIndex);

            var endOfBlock = decodeParms.GetBooleanOrDefault(NameToken.Create("EndOfBlock"), true);
            var columns = decodeParms.GetIntOrDefault(NameToken.Columns, 1728);
            var rowCount = decodeParms.GetIntOrDefault(NameToken.Rows, 0);
            var imageHeight = streamDictionary.GetIntOrDefault(NameToken.Height, NameToken.H, 0);
            // Rows = 0 is unknown even when the image dictionary declares Height.
            // EndOfBlock overrides Rows. Height limits stored image rows, never the encoded stopping condition.
            if (!UseLenientParsing && (columns <= 0 || rowCount < 0))
                throw new CorruptCompressedDataException($"Invalid CCITT image dimensions: columns={columns}, rows={rowCount}.");
            // MuPDF separates its streaming CCITT filter from its dimension-bounded image
            // reader (filter-fax.c and image.c). Apply that distinction here: retain at most
            // Height rows, but validate the remaining block and report its actual consumption.
            // Unlike PDFBox 3.0.8's fixed read, reaching Height does not stop input processing.
            // Sources: https://github.com/ArtifexSoftware/mupdf/blob/master/source/fitz/filter-fax.c
            // and https://github.com/ArtifexSoftware/mupdf/blob/master/source/fitz/image.c .
            // This implementation reuses PdfPig's row decoders; no MuPDF code is copied.
            var allowBitmapGrowth = endOfBlock || rowCount == 0;
            var maximumStoredRows = Math.Max(0, imageHeight);
            var capacityRows = allowBitmapGrowth ? maximumStoredRows
                : maximumStoredRows > 0 ? Math.Min(maximumStoredRows, Math.Max(0, rowCount)) : Math.Max(0, rowCount);
            var bitmapByteCount = GetDecodedBufferSize(columns, capacityRows, UseLenientParsing || capacityRows == 0);
            if (columns <= 0 || rowCount < 0)
                return Memory<byte>.Empty;

            var k = decodeParms.GetIntOrDefault(NameToken.K, 0);
            var encodedByteAlign = decodeParms.GetBooleanOrDefault(NameToken.EncodedByteAlign, false);
            if (input.IsEmpty)
            {
                if (UseLenientParsing)
                {
                    return Memory<byte>.Empty;
                }

                throw new CorruptCompressedDataException("Empty CCITT compressed data.");
            }

            var compressionType = DetermineCompressionType(input.Span, k, decodeParms);
            var requireEndOfLine = decodeParms.GetBooleanOrDefault(NameToken.EndOfLine, false);

            var blackIsOne = decodeParms.GetBooleanOrDefault(NameToken.BlackIs1, false);
            var damagedRowsBeforeError = decodeParms.GetIntOrDefault(NameToken.Create("DamagedRowsBeforeError"), 0);
            if (damagedRowsBeforeError < 0 && !UseLenientParsing)
                throw new CorruptCompressedDataException("Invalid CCITT DamagedRowsBeforeError value.");
            if (k < 0 || !requireEndOfLine)
                damagedRowsBeforeError = 0;
            damagedRowsBeforeError = Math.Max(0, damagedRowsBeforeError);
            // Retain compact input and reference-row state when a capacity hint is too small.
            // EndOfBlock selects marker handling; a positive Rows limit with EndOfBlock=false
            // remains fixed. Unknown Rows may grow until clean EOF without requiring a marker.
            long reservedBytes = 0;
            if (capacityRows > 0 && columns <= ushort.MaxValue)
            {
                var decodedBitmap = new byte[bitmapByteCount];
                if (CcittFaxCompactDecoder.TryDecodeBitmap(input.Span, ref decodedBitmap, columns,
                    capacityRows, compressionType, encodedByteAlign, blackIsOne, out int decodedRows, out bytesConsumed, out status, out reservedBytes,
                    true, endOfBlock, requireEndOfLine, verifyEndMarker: endOfBlock,
                    allowEndTolerance: endOfBlock, expectedRowsForEndTolerance: rowCount,
                    allowEndOfInput: !endOfBlock || rowCount == 0,
                    maximumGrowthBufferBytes: allowBitmapGrowth || maximumStoredRows > 0 ? MaximumDecodeBufferBytes : 0,
                    maximumStoredRows: maximumStoredRows, maximumDecodedRows: endOfBlock ? 0 : rowCount))
                    return decodedBitmap.AsMemory(0,
                        (maximumStoredRows > 0 ? Math.Min(decodedRows, maximumStoredRows) : decodedRows) * ((columns + 7) / 8));
                // A retry can overlap the previous bitmap and compact transition arrays until GC.
                // reservedBytes includes every bitmap allocated during this attempt.
            }
            return CcittFaxCompactDecoder.DecodeRowsToMemory(input.Span, columns, compressionType,
                encodedByteAlign, blackIsOne, UseLenientParsing, true, endOfBlock, damagedRowsBeforeError,
                out bytesConsumed, out status, MaximumDecodeBufferBytes - reservedBytes, requireEndOfLine,
                maximumRows: endOfBlock ? 0 : rowCount,
                requireEndMarker: endOfBlock && rowCount > 0,
                allowEndTolerance: endOfBlock, expectedRowsForEndTolerance: rowCount, maximumStoredRows: maximumStoredRows);
        }
        /// <summary>Checks dimensions and the buffer budget, then returns the bitmap byte count.</summary>
        internal static int GetDecodedBufferSize(int columns, int rowCount, bool useLenientParsing = false)
        {
            if (!useLenientParsing && (columns <= 0 || rowCount <= 0))
            {
                throw new CorruptCompressedDataException($"Invalid CCITT image dimensions: columns={columns}, rows={rowCount}.");
            }

            // Clamp dimensions separately: negative Rows must not cancel the work-array estimate
            // for positive Columns in lenient mode. Convert to Int64 before arithmetic; even the
            // largest Int32 dimensions keep this complete estimate within Int64.
            var nonnegativeColumns = Math.Max(0L, columns);
            var nonnegativeRows = Math.Max(0L, rowCount);
            var rowByteCount = (nonnegativeColumns + 7) / 8;
            var bitmapByteCount = rowByteCount * nonnegativeRows;
            var workingBufferByteCount = rowByteCount + 2 * (nonnegativeColumns + 2) * sizeof(int);
            var totalBufferByteCount = bitmapByteCount + workingBufferByteCount;

            if (totalBufferByteCount > MaximumDecodeBufferBytes)
            {
                throw new CorruptCompressedDataException(
                    $"CCITT decode buffers require {totalBufferByteCount} bytes for columns={columns}, rows={rowCount}; "
                    + $"at most {MaximumDecodeBufferBytes} bytes are allowed.");
            }

            // Passing the total limit ensures bitmap and transition-array lengths also fit in Int32.
            return (int)bitmapByteCount;
        }

        private static CcittFaxCompressionType DetermineCompressionType(ReadOnlySpan<byte> input, int k, DictionaryToken decodeParms)
        {
            if (k == 0)
            {
                if (decodeParms.ContainsKey(NameToken.EndOfLine))
                {
                    // PDFBOX-6080: an explicit EndOfLine parameter takes precedence over sniffing.
                    return decodeParms.GetBooleanOrDefault(NameToken.EndOfLine, false)
                        ? CcittFaxCompressionType.Group3_1D
                        : CcittFaxCompressionType.ModifiedHuffman;
                }

                var compressionType = CcittFaxCompressionType.Group3_1D;

                if (input.Length < 2 || input[0] != 0 || (input[1] >> 4 != 1 && input[1] != 1))
                {
                    // No leading end-of-line (EOL) code was found. Search the first 20 input
                    // bytes for 000000000001; otherwise use Modified Huffman without row EOLs.
                    compressionType = CcittFaxCompressionType.ModifiedHuffman;
                    var secondByte = input.Length > 1 ? input[1] : 0;
                    var eolWindow = (short)(((input[0] << 8) + secondByte) >> 4);
                    var headerBitCount = Math.Min(input.Length, 20) * 8;
                    for (var bitIndex = 12; bitIndex < headerBitCount; bitIndex++)
                    {
                        eolWindow = (short)((eolWindow << 1) + ((input[(bitIndex / 8)] >> (7 - (bitIndex % 8))) & 0x01));
                        if ((eolWindow & 0xFFF) == 1)
                        {
                            return CcittFaxCompressionType.Group3_1D;
                        }
                    }
                }

                return compressionType;
            }

            if (k > 0)
            {
                return CcittFaxCompressionType.Group3_2D;
            }

            return CcittFaxCompressionType.Group4_2D;
        }

    }
}