using System;
using System.IO;
using System.Runtime.CompilerServices;
using UglyToad.PdfPig.Fonts;

namespace UglyToad.PdfPig.Filters.CcittFax;
/// <summary>
/// Decodes Modified Huffman, Group 3 and Group 4 CCITT data directly into a packed bitmap.
/// </summary>
/// <remarks>
/// <para>Algorithm:</para>
/// <list type="number">
/// <item><description>Decode white and black runs through prefix lookup tables. Terminating
/// codes encode 0..63 pixels; makeup codes add multiples of 64 before a terminating code.</description></item>
/// <item><description>For 2D rows, store the preceding row's color-change positions. Horizontal
/// mode reads two runs, vertical mode offsets a reference transition, and pass mode skips a
/// reference transition pair.</description></item>
/// <item><description>Paint black intervals directly into rows initialized to white. Apply
/// BlackIs1 during painting, including white padding bits, to avoid a separate inversion pass.</description></item>
/// <item><description>Use UInt16 transitions for rows up to 65,535 pixels. If a row is wider or
/// compact decoding encounters invalid or truncated input, restart from the original compressed
/// input using signed Int32 transitions and the requested strict/lenient parsing policy.</description></item>
/// </list>
/// <para>The caller validates dimensions, output capacity and the allocation budget before
/// entering this decoder. Runs are bounded while decoding; transition counts and a compact-path
/// operation limit also prevent repeated zero-length runs from growing the row state indefinitely.</para>
/// <para>Implementation provenance:</para>
/// <list type="table">
/// <listheader><term>Source</term><description>Reused material or adapted idea</description></listheader>
/// <item><term>PdfPig / Apache PDFBox decoder (Apache-2.0)</term><description>
/// Standard T.4/T.6 codewords and run, reference-row and malformed-row rules.
/// See <see href="https://github.com/UglyToad/PdfPig/blob/bdbc5f47fdbca11542db7ee876426ee601374427/src/UglyToad.PdfPig/Filters/CcittFax/CcittFaxDecoderStream.cs">pinned PdfPig source</see>
/// and its attributed <see href="https://github.com/apache/pdfbox/blob/e644c29279e276bde14ce7a33bdeef0cb1001b3e/pdfbox/src/main/java/org/apache/pdfbox/filter/CCITTFaxDecoderStream.java">PDFBox source</see>.
/// </description></item>
/// <item><term>libdeflate fast loop (MIT)</term><description>Idea of preparing the next operation
/// before completing independent output work. This decoder peeks the next CCITT mode before
/// painting and consumes it on the next iteration. No libdeflate code or tables were copied.
/// See <see href="https://github.com/ebiggers/libdeflate/blob/8ae3a19a59173f16e3738d45b54a1cdf05ad12d4/lib/decompress_template.h">decompression loop</see>.
/// </description></item>
/// <item><term>This PdfPig implementation (Apache-2.0)</term><description>The flat lookup builders,
/// packed short-run pairs, compact transition storage, polarity-aware interval painter, generic
/// row policies and compact-to-signed dispatch are implemented here for CCITT.</description></item>
/// </list>
/// <para>The bit-reader refill scheduling and its sources are documented on
/// <see cref="CcittFaxCompactBitReader"/>. The signed recovery algorithm is documented on
/// <see cref="SignedRowDecoder"/>.</para>
/// </remarks>
internal static partial class CcittFaxCompactDecoder
{
    private const int HorizontalMode = 100;
    private const int PassMode = 101;
    private const int UnknownMode = int.MinValue;
    private const int EndOfLineRunMarker = -2000;

    // Bits 0..3 store the consumed code length; bits 4..15 store its pixel count.
    // A zero entry marks an unmapped prefix, not a zero-pixel run. A run uses zero or more
    // makeup codes (multiples of 64), then a terminating code (0..63).
    private static readonly ushort[] WhiteRunLookup = BuildRunLookup(CcittFaxCodebook.WhiteRunCodes, 12);
    private static readonly ushort[] BlackRunLookup = BuildRunLookup(CcittFaxCodebook.BlackRunCodes, 13);

    private static ushort[] BuildRunLookup(CcittCode[] runCodes, int lookaheadBitCount)
    {
        var lookup = new ushort[1 << lookaheadBitCount];
        foreach (var code in runCodes)
        {
#if NET8_0_OR_GREATER
            Array.Fill(lookup, checked((ushort)((code.Run << 4) | code.Length)), code.Bits << (lookaheadBitCount - code.Length), 1 << (lookaheadBitCount - code.Length));
#else
            lookup.AsSpan(code.Bits << (lookaheadBitCount - code.Length), 1 << (lookaheadBitCount - code.Length)).Fill(checked((ushort)((code.Run << 4) | code.Length)));
#endif
        }
        return lookup;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeRunLength<T>(ref CcittFaxCompactBitReader bitReader, bool isWhiteRun, int remainingPixels)
        where T : struct, ICcittRowPolicy
    {
        var lookup = isWhiteRun ? WhiteRunLookup : BlackRunLookup;
        int lookaheadBitCount = isWhiteRun ? 12 : 13;
        int accumulatedRunLength = 0;
        int runLength;
        do
        {
            int lookupEntry = lookup[bitReader.PeekBits<T>(lookaheadBitCount)];
            if (lookupEntry == 0)
                throw new InvalidDataException("Invalid CCITT Huffman code.");
            bitReader.ConsumeBits(lookupEntry & 15);
            runLength = lookupEntry >> 4;
            if (runLength > remainingPixels - accumulatedRunLength)
                throw new InvalidDataException("CCITT run exceeds row bounds.");
            accumulatedRunLength += runLength;
        }
        while (runLength >= 64);
        return accumulatedRunLength;
    }

    // Ordinary T.4/T.6 2D operations fit in seven bits. Each code fills all entries beginning
    // with that code, replacing bit-by-bit tree traversal with one lookup. Unmapped prefixes
    // use the bit-by-bit reader, which can trigger compatibility decoding.
    private static readonly int[] ModeLookup = BuildModeLookup();
    // Bits 0..2 store the code length. The remaining bits store a signed vertical offset
    // (-3..3), HorizontalMode (two runs), or PassMode (skip two reference transitions).
    private static int[] BuildModeLookup()
    {
        var lookup = new int[128];
        foreach (var (codeBits, codeBitCount, operation) in new[]
        {
            (1, 1, 0),
            (3, 3, 1),
            (2, 3, -1),
            (1, 3, HorizontalMode),
            (1, 4, PassMode),
            (3, 6, 2),
            (2, 6, -2),
            (3, 7, 3),
            (2, 7, -3)
        })
        {
#if NET8_0_OR_GREATER
            Array.Fill(lookup, (operation << 3) | codeBitCount, codeBits << (7 - codeBitCount), 1 << (7 - codeBitCount));
#else
            lookup.AsSpan(codeBits << (7 - codeBitCount), 1 << (7 - codeBitCount)).Fill((operation << 3) | codeBitCount);
#endif
        }
        return lookup;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeMode<T>(ref CcittFaxCompactBitReader bitReader)
        where T : struct, ICcittRowPolicy
    {
        int lookupEntry = ModeLookup[bitReader.PeekBits<T>(7)];
        if (lookupEntry == 0)
            return bitReader.ReadModeBitByBit<T>();
        bitReader.ConsumeBits(lookupEntry & 7);
        return lookupEntry >> 3;
    }

#if NET8_0_OR_GREATER
    /// <summary>Specializes Group 4 decoding with black pixels represented by one bits.</summary>
    private readonly struct Group4BlackIsOnePolicy : ICcittRowPolicy
    {
        public static bool IsGroup4 => true;
        public static bool BlackIsOne => true;
    }

    /// <summary>Specializes Group 4 decoding with black pixels represented by zero bits.</summary>
    private readonly struct Group4BlackIsZeroPolicy : ICcittRowPolicy
    {
        public static bool IsGroup4 => true;
        public static bool BlackIsOne => false;
    }

    /// <summary>Selects row framing from the compression type and represents black pixels by one bits.</summary>
    private readonly struct GeneralBlackIsOnePolicy : ICcittRowPolicy
    {
        public static bool IsGroup4 => false;
        public static bool BlackIsOne => true;
    }

    /// <summary>Selects row framing from the compression type and represents black pixels by zero bits.</summary>
    private readonly struct GeneralBlackIsZeroPolicy : ICcittRowPolicy
    {
        public static bool IsGroup4 => false;
        public static bool BlackIsOne => false;
    }

#else
    /// <summary>Specializes Group 4 decoding with black pixels represented by one bits.</summary>
    private readonly struct Group4BlackIsOnePolicy : ICcittRowPolicy
    {
        public bool IsGroup4 => true;
        public bool BlackIsOne => true;
    }

    /// <summary>Specializes Group 4 decoding with black pixels represented by zero bits.</summary>
    private readonly struct Group4BlackIsZeroPolicy : ICcittRowPolicy
    {
        public bool IsGroup4 => true;
        public bool BlackIsOne => false;
    }

    /// <summary>Selects row framing from the compression type and represents black pixels by one bits.</summary>
    private readonly struct GeneralBlackIsOnePolicy : ICcittRowPolicy
    {
        public bool IsGroup4 => false;
        public bool BlackIsOne => true;
    }

    /// <summary>Selects row framing from the compression type and represents black pixels by zero bits.</summary>
    private readonly struct GeneralBlackIsZeroPolicy : ICcittRowPolicy
    {
        public bool IsGroup4 => false;
        public bool BlackIsOne => false;
    }

#endif
    /// <summary>
    /// Builds two 12-bit prefix tables that decode a pair of short terminating runs in one lookup.
    /// </summary>
    /// <remarks>
    /// One table starts with white and the other with black. Bits 0..5 and 6..11 store the
    /// two terminating pixel counts; bits 12..15 store their combined code length. A zero
    /// entry means decode the runs individually. Only pairs of at most 12 bits fit the lookup.
    /// Tables are generated from <see cref="CcittFaxCodebook"/>.
    /// </remarks>
    private static class RunPairLookup
    {
        internal static readonly ushort[] WhiteFirstPairs = BuildRunPairLookup(true);
        internal static readonly ushort[] BlackFirstPairs = BuildRunPairLookup(false);

        private static ushort[] BuildRunPairLookup(bool firstRunIsWhite)
        {
            var lookup = new ushort[4096];
            foreach (var firstCode in firstRunIsWhite ? CcittFaxCodebook.WhiteRunCodes : CcittFaxCodebook.BlackRunCodes)
            {
                foreach (var secondCode in firstRunIsWhite ? CcittFaxCodebook.BlackRunCodes : CcittFaxCodebook.WhiteRunCodes)
                {
                    int combinedBitCount = firstCode.Length + secondCode.Length;
                    if (firstCode.Run >= 64 || secondCode.Run >= 64 || combinedBitCount > 12)
                        continue;
                    ushort lookupEntry = checked((ushort)(firstCode.Run | (secondCode.Run << 6) | (combinedBitCount << 12)));
                    int combinedCodeBits = (firstCode.Bits << secondCode.Length) | secondCode.Bits;
#if NET8_0_OR_GREATER
                    Array.Fill(lookup, lookupEntry, combinedCodeBits << (12 - combinedBitCount), 1 << (12 - combinedBitCount));
#else
                    lookup.AsSpan(combinedCodeBits << (12 - combinedBitCount), 1 << (12 - combinedBitCount)).Fill(lookupEntry);
#endif
                }
            }

            return lookup;
        }
    }

    /// <summary>Attempts direct bitmap decoding with UInt16 color-change positions.</summary>
    /// <param name="compressedInput">CCITT bytes starting at the first encoded row.</param>
    /// <param name="decodedBitmap">Caller-allocated bitmap with space for every requested row.</param>
    /// <param name="columns">Validated positive row width in pixels.</param>
    /// <param name="rowCount">Validated positive number of output rows.</param>
    /// <param name="compressionType">Resolved row framing and coding family.</param>
    /// <param name="encodedByteAlign">Whether each row starts at a byte boundary.</param>
    /// <param name="blackIsOne">Whether black pixels are represented by one bits.</param>
    /// <param name="acceptOptionalEndOfLine">Accepts optional framing EOL in Modified Huffman rows.</param>
    /// <param name="endOfBlock">Stops at complete EOFB/RTC markers and pads the remaining bitmap white.</param>
    /// <param name="requireEndOfLine">Requires EOL before Group 3 rows; false permits optional framing.</param>
    /// <returns>True when the bitmap was completed on the compact path; otherwise false.
    /// A width above UInt16 returns without changing output. Invalid or truncated input clears
    /// output so the caller can restart through the signed decoder.</returns>
    internal static bool TryDecode(
        ReadOnlySpan<byte> compressedInput,
        byte[] decodedBitmap,
        int columns,
        int rowCount,
        CcittFaxCompressionType compressionType,
        bool encodedByteAlign,
        bool blackIsOne,
        bool acceptOptionalEndOfLine = false,
        bool endOfBlock = true,
        bool requireEndOfLine = true)
    => TryDecodeBitmap(compressedInput, decodedBitmap, columns, rowCount, compressionType,
        encodedByteAlign, blackIsOne, out _, acceptOptionalEndOfLine, endOfBlock, requireEndOfLine);

    /// <summary>Reports complete rows, optionally verifying termination after the capacity hint.</summary>
    /// <remarks>End tolerance permits shortened Group 3 RTCs after complete rows. Clean EOF and a
    /// single Group 3/4 EOL additionally require an exact positive expectedRowsForEndTolerance count.</remarks>
    internal static bool TryDecodeBitmap(ReadOnlySpan<byte> compressedInput, byte[] decodedBitmap,
        int columns, int rowCount, CcittFaxCompressionType compressionType, bool encodedByteAlign,
        bool blackIsOne, out int decodedRowCount, bool acceptOptionalEndOfLine,
        bool endOfBlock, bool requireEndOfLine, bool verifyEndMarker = false, bool allowEndTolerance = false,
        int expectedRowsForEndTolerance = 0, bool allowEndOfInput = false)
        => TryDecodeBitmap(compressedInput, decodedBitmap, columns, rowCount, compressionType,
            encodedByteAlign, blackIsOne, out decodedRowCount, out _, out _, acceptOptionalEndOfLine,
            endOfBlock, requireEndOfLine, verifyEndMarker, allowEndTolerance, expectedRowsForEndTolerance, allowEndOfInput);

    /// <summary>Reports complete rows and byte-rounded consumption; failed attempts have no usable boundary.</summary>
    /// <remarks>rowCount is bitmap capacity, not necessarily PDF Rows. expectedRowsForEndTolerance
    /// is the separate PDF value (zero means unknown). allowEndOfInput admits clean EOF without
    /// a declared count; allowEndTolerance additionally enables the narrow known-Rows and
    /// shortened-marker exceptions. A false return clears output and consumption: the caller
    /// must restart the growing decoder from the original input, not this attempt's lookahead.</remarks>
    internal static bool TryDecodeBitmap(ReadOnlySpan<byte> compressedInput, byte[] decodedBitmap,
        int columns, int rowCount, CcittFaxCompressionType compressionType, bool encodedByteAlign,
        bool blackIsOne, out int decodedRowCount, out int bytesConsumed, out CcittFaxDecodeStatus status,
        bool acceptOptionalEndOfLine, bool endOfBlock, bool requireEndOfLine,
        bool verifyEndMarker = false, bool allowEndTolerance = false,
        int expectedRowsForEndTolerance = 0, bool allowEndOfInput = false)
        => TryDecodeBitmap(compressedInput, ref decodedBitmap, columns, rowCount, compressionType,
            encodedByteAlign, blackIsOne, out decodedRowCount, out bytesConsumed, out status,
            out _, acceptOptionalEndOfLine, endOfBlock, requireEndOfLine, verifyEndMarker,
            allowEndTolerance, expectedRowsForEndTolerance, allowEndOfInput);

    /// <summary>Decodes with optional bounded bitmap growth, preserving input and reference-row state.</summary>
    /// <remarks>A positive growth budget permits rows beyond the initial capacity. Every allocated
    /// bitmap, including replaced arrays awaiting collection, and both transition arrays count
    /// towards allocatedBufferBytes. The caller reserves that amount if signed decoding retries.
    /// Marker and clean-EOF checks precede growth, so termination exactly at capacity needs no
    /// new allocation. With EndOfBlock=false and unknown PDF Rows, the caller may supply Height
    /// as initial capacity and a growth budget; Height does not become a row limit.
    /// maximumStoredRows limits image output only: excess rows are validated in a reusable scratch
    /// row until the encoded stopping condition. decodedRowCount still counts all complete rows.
    /// maximumDecodedRows supplies the independent positive PDF Rows limit when EndOfBlock=false.
    /// The growth budget also bounds logical decoded bytes when image output is capped.
    /// A zero budget retains the fixed-capacity behavior used by bitmap callers.</remarks>
    internal static bool TryDecodeBitmap(ReadOnlySpan<byte> compressedInput, ref byte[] decodedBitmap,
        int columns, int rowCount, CcittFaxCompressionType compressionType, bool encodedByteAlign,
        bool blackIsOne, out int decodedRowCount, out int bytesConsumed, out CcittFaxDecodeStatus status, out long allocatedBufferBytes,
        bool acceptOptionalEndOfLine, bool endOfBlock, bool requireEndOfLine,
        bool verifyEndMarker = false, bool allowEndTolerance = false,
        int expectedRowsForEndTolerance = 0, bool allowEndOfInput = false, long maximumGrowthBufferBytes = 0,
        int maximumStoredRows = 0, int maximumDecodedRows = 0)
    {
        allocatedBufferBytes = decodedBitmap.Length + 2L * (columns + 2) * sizeof(ushort);
        if (maximumGrowthBufferBytes > 0 && allocatedBufferBytes > maximumGrowthBufferBytes)
            throw new CorruptCompressedDataException("CCITT compact decode buffers exceed the allocation budget.");
        decodedRowCount = 0;
        bytesConsumed = 0;
        status = CcittFaxDecodeStatus.NotCompleted;
        if (columns > ushort.MaxValue)
            return false;
        try
        {
            var bitReader = new CcittFaxCompactBitReader(compressedInput);
            CompactDecodeResult result;
            if (compressionType == CcittFaxCompressionType.Group4_2D)
            {
                if (blackIsOne)
                    result = DecodeCompactRows<Group4BlackIsOnePolicy>(ref bitReader, ref decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, acceptOptionalEndOfLine, endOfBlock, requireEndOfLine, verifyEndMarker, allowEndTolerance, expectedRowsForEndTolerance, allowEndOfInput, maximumGrowthBufferBytes, ref allocatedBufferBytes, maximumStoredRows, maximumDecodedRows);
                else
                    result = DecodeCompactRows<Group4BlackIsZeroPolicy>(ref bitReader, ref decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, acceptOptionalEndOfLine, endOfBlock, requireEndOfLine, verifyEndMarker, allowEndTolerance, expectedRowsForEndTolerance, allowEndOfInput, maximumGrowthBufferBytes, ref allocatedBufferBytes, maximumStoredRows, maximumDecodedRows);
            }
            else
            {
                if (blackIsOne)
                    result = DecodeCompactRows<GeneralBlackIsOnePolicy>(ref bitReader, ref decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, acceptOptionalEndOfLine, endOfBlock, requireEndOfLine, verifyEndMarker, allowEndTolerance, expectedRowsForEndTolerance, allowEndOfInput, maximumGrowthBufferBytes, ref allocatedBufferBytes, maximumStoredRows, maximumDecodedRows);
                else
                    result = DecodeCompactRows<GeneralBlackIsZeroPolicy>(ref bitReader, ref decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, acceptOptionalEndOfLine, endOfBlock, requireEndOfLine, verifyEndMarker, allowEndTolerance, expectedRowsForEndTolerance, allowEndOfInput, maximumGrowthBufferBytes, ref allocatedBufferBytes, maximumStoredRows, maximumDecodedRows);
            }

            decodedRowCount = result.Rows;
            bytesConsumed = bitReader.BytesConsumed;
            status = result.Status;
            return true;
        }
        catch (InvalidDataException)
        {
            bytesConsumed = 0;
            status = CcittFaxDecodeStatus.NotCompleted;
            decodedBitmap.AsSpan().Clear();
            return false;
        }
    }

    // Paint black pixels from start (inclusive) to end (exclusive). Callers initialize the row
    // to white first. Edge masks preserve pixels outside the interval, including row padding.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PaintBlackInterval<T>(Span<byte> rowPixels, int startPosition, int endPosition)
        where T : struct, ICcittRowPolicy
    {
        if (endPosition <= startPosition)
            return;
        int firstByteIndex = startPosition >> 3;
        int lastByteIndex = (endPosition - 1) >> 3;
        int firstByteMask = 255 >> (startPosition & 7);
        int lastByteMask = 255 << (7 - ((endPosition - 1) & 7));
        if (firstByteIndex == lastByteIndex)
        {
            byte pixelMask = (byte)(firstByteMask & lastByteMask);
#if NET8_0_OR_GREATER
            rowPixels[firstByteIndex] = T.BlackIsOne ? (byte)(rowPixels[firstByteIndex] | pixelMask) : (byte)(rowPixels[firstByteIndex] & ~pixelMask);
#else
            rowPixels[firstByteIndex] = default(T).BlackIsOne ? (byte)(rowPixels[firstByteIndex] | pixelMask) : (byte)(rowPixels[firstByteIndex] & ~pixelMask);
#endif
            return;
        }

#if NET8_0_OR_GREATER
        rowPixels[firstByteIndex] = T.BlackIsOne ? (byte)(rowPixels[firstByteIndex] | firstByteMask) : (byte)(rowPixels[firstByteIndex] & ~firstByteMask);
#else
        rowPixels[firstByteIndex] = default(T).BlackIsOne ? (byte)(rowPixels[firstByteIndex] | firstByteMask) : (byte)(rowPixels[firstByteIndex] & ~firstByteMask);
#endif
#if NET8_0_OR_GREATER
        rowPixels.Slice(firstByteIndex + 1, lastByteIndex - firstByteIndex - 1).Fill(T.BlackIsOne ? (byte)255 : (byte)0);
#else
        rowPixels.Slice(firstByteIndex + 1, lastByteIndex - firstByteIndex - 1).Fill(default(T).BlackIsOne ? (byte)255 : (byte)0);
#endif
#if NET8_0_OR_GREATER
        rowPixels[lastByteIndex] = T.BlackIsOne ? (byte)(rowPixels[lastByteIndex] | lastByteMask) : (byte)(rowPixels[lastByteIndex] & ~lastByteMask);
#else
        rowPixels[lastByteIndex] = default(T).BlackIsOne ? (byte)(rowPixels[lastByteIndex] | lastByteMask) : (byte)(rowPixels[lastByteIndex] & ~lastByteMask);
#endif
    }

    // Return metadata only after the row loop; no out-parameter references stay live in that loop.
    private readonly struct CompactDecodeResult
    {
        internal readonly int Rows;
        internal readonly CcittFaxDecodeStatus Status;

        internal CompactDecodeResult(int rows, CcittFaxDecodeStatus status)
        {
            Rows = rows;
            Status = status;
        }
    }

    private static CompactDecodeResult DecodeCompactRows<T>(
        ref CcittFaxCompactBitReader bitReader,
        ref byte[] decodedBitmap,
        int columns,
        int rowCount,
        CcittFaxCompressionType compressionType,
        bool encodedByteAlign,
        bool acceptOptionalEndOfLine,
        bool endOfBlock,
        bool requireEndOfLine,
        bool verifyEndMarker,
        bool allowEndTolerance, int expectedRowsForEndTolerance, bool allowEndOfInput,
        long maximumGrowthBufferBytes, ref long allocatedBufferBytes, int maximumStoredRows, int maximumDecodedRows)
        where T : struct, ICcittRowPolicy
    {
        int rowByteCount = (columns + 7) / 8;
        // Reuse two transition arrays: the previous row is the 2D reference, the current
        // row records newly decoded color changes. Only entries below each row count are valid.
        // Equal positions represent zero-length runs and must remain in their original order.
        var referenceTransitions = new ushort[columns + 2];
        var currentTransitions = new ushort[columns + 2];
        int referenceTransitionCount = 0;
        byte[]? discardedRow = null;
        int rowIndex = 0;
        for (; (rowIndex < rowCount || maximumGrowthBufferBytes > 0 || maximumStoredRows > 0)
            && (maximumDecodedRows == 0 || rowIndex < maximumDecodedRows); rowIndex++)
        {
            // rowIndex counts completed rows; rowCount is merely this bitmap's capacity.
            // Compact rows are never substituted. Only the independent PDF Rows value can
            // authorize known-height EOF or a single Group 3/4 EOL, not the Height hint.
            bool exactDeclaredRows = expectedRowsForEndTolerance > 0 && rowIndex == expectedRowsForEndTolerance;
            // Unknown Rows allow final-byte padding only. Exact positive Rows additionally
            // authorize all-zero fill through EOF, including whole bytes. This local policy
            // trusts the intact declared image even though a truncated extra code can be all zero.
            bool cleanEndOfInput = rowIndex > 0 && ((allowEndOfInput && bitReader.HasOnlyBytePadding)
                || allowEndTolerance && exactDeclaredRows && bitReader.TryReadZeroFillToEnd());
            bool allowSingleEol = allowEndTolerance && exactDeclaredRows
                && (!encodedByteAlign || bitReader.HasZeroAlignmentPadding);
            if (encodedByteAlign)
                bitReader.AlignToByteBoundary();
            if (endOfBlock)
            {
                var endStatus = bitReader.ReadEndOfBlockStatus(compressionType,
                    allowIncompleteRtc: allowEndTolerance && rowIndex > 0
                        && (expectedRowsForEndTolerance == 0 || exactDeclaredRows),
                    allowIncompleteEofb: allowSingleEol, allowSingleGroup3Eol: allowSingleEol);
                if (endStatus != CcittFaxDecodeStatus.NotCompleted)
                {
                    decodedBitmap.AsSpan(Math.Min(rowIndex, rowCount) * rowByteCount).Fill(BlackIsOne<T>() ? (byte)0 : (byte)255);
                    bitReader.AlignToByteBoundary();
                    return new CompactDecodeResult(rowIndex, endStatus);
                }
            }
            // EOF follows complete rows with final-byte padding or authorized declared-Rows zero fill.
            if (cleanEndOfInput)
            {
                bitReader.AlignToByteBoundary();
                return new CompactDecodeResult(rowIndex, CcittFaxDecodeStatus.EndOfInput);
            }
            // The image output limit is independent of the encoded block's end. Decode
            // excess rows into one reusable scratch row, retaining transitions for 2D coding.
            // Count their logical decoded bytes too: discarding output must not remove the
            // existing finite work bound supplied by the decode-buffer limit.
            bool discardRow = maximumStoredRows > 0 && rowIndex >= maximumStoredRows;
            if (discardRow && (long)(rowIndex + 1) * rowByteCount > maximumGrowthBufferBytes)
                throw new CorruptCompressedDataException("CCITT decoded rows exceed the work budget.");
            if (discardRow && discardedRow is null)
            {
                if (allocatedBufferBytes + rowByteCount > maximumGrowthBufferBytes)
                    throw new CorruptCompressedDataException("CCITT compact decode buffers exceed the allocation budget.");
                discardedRow = new byte[rowByteCount];
                allocatedBufferBytes += rowByteCount;
            }
            if (rowIndex == rowCount && !discardRow)
            {
                // Account for all replaced arrays, not just the current live bitmap. Clamp
                // geometric growth to the remaining budget in complete rows before allocating.
                long affordableRows = (maximumGrowthBufferBytes - allocatedBufferBytes) / rowByteCount;
                long newRowCount = Math.Min(Math.Max((long)rowCount + 1, 2L * rowCount), affordableRows);
                if (newRowCount <= rowCount)
                    throw new CorruptCompressedDataException("CCITT compact decode buffers exceed the allocation budget.");
                int newByteCount = checked((int)(newRowCount * rowByteCount));
                Array.Resize(ref decodedBitmap, newByteCount);
                allocatedBufferBytes += newByteCount;
                rowCount = (int)newRowCount;
            }
            var rowPixels = discardRow ? discardedRow.AsSpan() : decodedBitmap.AsSpan(rowIndex * rowByteCount, rowByteCount);
#if NET8_0_OR_GREATER
            rowPixels.Fill(T.BlackIsOne ? (byte)0 : (byte)255);
#else
            rowPixels.Fill(default(T).BlackIsOne ? (byte)0 : (byte)255);
#endif
            bool isOneDimensional;
#if NET8_0_OR_GREATER
            if (T.IsGroup4)
#else
            if (default(T).IsGroup4)
#endif
            {
                if (acceptOptionalEndOfLine)
                {
                    bool hasEol = bitReader.TryReadEndOfLine();
                    if (requireEndOfLine && !hasEol)
                        throw new InvalidDataException("Required CCITT EOL is missing.");
                }
                isOneDimensional = false;
            }
            else
                isOneDimensional = bitReader.ReadRowIsOneDimensional<T>(compressionType,
                    false, acceptOptionalEndOfLine, requireEndOfLine);
            bool isWhiteRun = true;
            int pixelPosition = 0;
            int transitionCount = 0;
            int referenceTransitionIndex = 0;
            int operationCount = 0;
            int whiteReferenceIndex = 0;
            int blackReferenceIndex = 1;
            int nextModeEntry = 0;
            bool hasNextModeEntry = false;
            while (pixelPosition < columns)
            {
                // A zero-length run does not advance pixelPosition. Limit the number of operations too,
                // so repeated empty runs cannot keep the compact loop busy indefinitely.
                if (++operationCount > 2L * columns + 4)
                    throw new InvalidDataException("CCITT row does not progress.");
                int operation;
                if (isOneDimensional)
                    operation = 0;
                else if (hasNextModeEntry)
                {
                    hasNextModeEntry = false;
                    if (nextModeEntry == 0)
                        operation = bitReader.ReadModeBitByBit<T>();
                    else
                    {
                        bitReader.ConsumeBits(nextModeEntry & 7);
                        operation = nextModeEntry >> 3;
                    }
                }
                else
                    operation = DecodeMode<T>(ref bitReader);
                if (isOneDimensional || operation == HorizontalMode)
                {
                    uint packedRunPair = (isWhiteRun ? RunPairLookup.WhiteFirstPairs : RunPairLookup.BlackFirstPairs)[bitReader.PeekBits<T>(12)];
                    int firstRunLength = (int)(packedRunPair & 63);
                    int secondRunLength = (int)((packedRunPair >> 6) & 63);
                    // In 1D, a first run that completes the row must not consume a second run
                    // from the following row. A horizontal 2D operation always contains two runs.
                    if (packedRunPair != 0
                        && firstRunLength + secondRunLength <= columns - pixelPosition
                        && (!isOneDimensional || firstRunLength < columns - pixelPosition))
                    {
                        if (currentTransitions.Length - transitionCount < 2)
                            throw new InvalidDataException("Too many CCITT transitions.");
                        bitReader.ConsumeBits((int)(packedRunPair >> 12));
                        int firstRunEnd = pixelPosition + firstRunLength;
                        int secondRunEnd = firstRunEnd + secondRunLength;
                        if (!isOneDimensional)
                        {
                            // Peek the next operation before painting to overlap independent work.
                            // Its bits are consumed only at the next loop iteration.
                            nextModeEntry = ModeLookup[bitReader.PeekBits<T>(7)];
                            hasNextModeEntry = true;
                        }

                        PaintBlackInterval<T>(rowPixels, isWhiteRun ? firstRunEnd : pixelPosition, isWhiteRun ? secondRunEnd : firstRunEnd);
                        currentTransitions[transitionCount++] = checked((ushort)firstRunEnd);
                        currentTransitions[transitionCount++] = checked((ushort)secondRunEnd);
                        pixelPosition = secondRunEnd;
                        continue;
                    }

                    if (!isOneDimensional)
                    {
                        int firstRunEnd = pixelPosition + DecodeRunLength<T>(ref bitReader, isWhiteRun, columns - pixelPosition);
                        if (transitionCount == currentTransitions.Length)
                            throw new InvalidDataException("Too many CCITT transitions.");
                        currentTransitions[transitionCount++] = checked((ushort)firstRunEnd);
                        int secondRunEnd = firstRunEnd + DecodeRunLength<T>(ref bitReader, !isWhiteRun, columns - firstRunEnd);
                        nextModeEntry = ModeLookup[bitReader.PeekBits<T>(7)];
                        hasNextModeEntry = true;
                        PaintBlackInterval<T>(rowPixels, isWhiteRun ? firstRunEnd : pixelPosition, isWhiteRun ? secondRunEnd : firstRunEnd);
                        if (transitionCount == currentTransitions.Length)
                            throw new InvalidDataException("Too many CCITT transitions.");
                        currentTransitions[transitionCount++] = checked((ushort)secondRunEnd);
                        pixelPosition = secondRunEnd;
                        continue;
                    }

                    int nextPosition = pixelPosition + DecodeRunLength<T>(ref bitReader, isWhiteRun, columns - pixelPosition);
                    if (!isWhiteRun)
                        PaintBlackInterval<T>(rowPixels, pixelPosition, nextPosition);
                    if (transitionCount == currentTransitions.Length)
                        throw new InvalidDataException("Too many CCITT transitions.");
                    currentTransitions[transitionCount++] = checked((ushort)nextPosition);
                    pixelPosition = nextPosition;
                    isWhiteRun = !isWhiteRun;
                }
                else
                {
                    nextModeEntry = ModeLookup[bitReader.PeekBits<T>(7)];
                    hasNextModeEntry = true;

                    // Even and odd cursors track the next reference transition for each color.
                    // They only advance because pixelPosition never moves backwards. At the row start,
                    // retain an initial zero-position transition; it changes the color.
                    referenceTransitionIndex = isWhiteRun ? whiteReferenceIndex : blackReferenceIndex;
                    while (referenceTransitionIndex < referenceTransitionCount && pixelPosition != 0 && referenceTransitions[referenceTransitionIndex] <= pixelPosition)
                        referenceTransitionIndex += 2;
                    if (isWhiteRun)
                        whiteReferenceIndex = referenceTransitionIndex;
                    else
                        blackReferenceIndex = referenceTransitionIndex;
                    // The signed path can wrap this pass to the first reference transition
                    // when no later transition exists. Retry there instead of substituting width;
                    // its position validation applies the requested strict/lenient policy.
                    if (operation == PassMode
                        && pixelPosition != 0
                        && referenceTransitionCount != 0
                        && referenceTransitionIndex >= referenceTransitionCount)
                        throw new InvalidDataException("CCITT pass requires compatibility decoding.");
                    // The first reference transition is b1 in the T.4/T.6 notation.
                    int referencePosition = referenceTransitionIndex < referenceTransitionCount
                        ? referenceTransitions[referenceTransitionIndex]
                        : columns;
                    int nextPosition = operation == PassMode
                        ? (referenceTransitionIndex + 1 < referenceTransitionCount ? referenceTransitions[referenceTransitionIndex + 1] : columns)
                        : referencePosition + operation;
                    if (nextPosition < pixelPosition || nextPosition > columns)
                        throw new InvalidDataException("Invalid CCITT vertical/pass position.");
                    if (!isWhiteRun)
                        PaintBlackInterval<T>(rowPixels, pixelPosition, nextPosition);
                    pixelPosition = nextPosition;
                    if (operation != PassMode)
                    {
                        if (transitionCount == currentTransitions.Length)
                            throw new InvalidDataException("Too many CCITT transitions.");
                        currentTransitions[transitionCount++] = checked((ushort)pixelPosition);
                        isWhiteRun = !isWhiteRun;
                    }
                }
            }

            (referenceTransitions, currentTransitions) = (currentTransitions, referenceTransitions);
            referenceTransitionCount = transitionCount;
        }
        var status = CcittFaxDecodeStatus.RowLimit;
        if (verifyEndMarker)
        {
            // A full bitmap does not prove the image ended: Height can be too small. Apply
            // the same trailer rules as at an earlier row boundary. If none matches, fail
            // this compact attempt so the growing path can decode further complete rows.
            bool exactDeclaredRows = expectedRowsForEndTolerance > 0 && rowCount == expectedRowsForEndTolerance;
            bool cleanEndOfInput = (allowEndOfInput && bitReader.HasOnlyBytePadding)
                || allowEndTolerance && exactDeclaredRows && bitReader.TryReadZeroFillToEnd();
            bool allowSingleEol = allowEndTolerance && exactDeclaredRows
                && (!encodedByteAlign || bitReader.HasZeroAlignmentPadding);
            if (encodedByteAlign)
                bitReader.AlignToByteBoundary();
            status = bitReader.ReadEndOfBlockStatus(compressionType,
                allowIncompleteRtc: allowEndTolerance && rowCount > 0
                    && (expectedRowsForEndTolerance == 0 || exactDeclaredRows),
                allowIncompleteEofb: allowSingleEol, allowSingleGroup3Eol: allowSingleEol);
            if (status == CcittFaxDecodeStatus.NotCompleted && cleanEndOfInput)
                status = CcittFaxDecodeStatus.EndOfInput;
            if (status == CcittFaxDecodeStatus.NotCompleted)
                throw new InvalidDataException("CCITT end marker not found at the bitmap capacity hint.");
        }
        bitReader.AlignToByteBoundary();
        return new CompactDecodeResult(rowIndex, status);
    }
}
