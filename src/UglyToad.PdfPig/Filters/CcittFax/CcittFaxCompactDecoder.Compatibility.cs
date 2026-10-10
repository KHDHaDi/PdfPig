using System;
using System.IO;
using System.Runtime.CompilerServices;
using UglyToad.PdfPig.Fonts;

namespace UglyToad.PdfPig.Filters.CcittFax;
internal static partial class CcittFaxCompactDecoder
{
    /// <summary>
    /// Decodes CCITT rows with the compact loop optimizations and signed color-change positions.
    /// </summary>
    /// <remarks>
    /// <para>Algorithm: decode the row's EOL/tag framing, then execute 1D runs or 2D horizontal,
    /// vertical and pass operations against the previous row's transitions. Int32 positions can
    /// represent wide rows and invalid negative or backwards transitions for policy validation.</para>
    /// <para>Ordered rows use separate monotone reference cursors for white and black, packed
    /// short-run pairs, one interval paint per horizontal pair, and prefetched ordinary 2D modes.
    /// These are the same loop optimizations as UInt16 decoding. Exact reads handle short tails
    /// and unknown prefixes without speculative truncation failures.</para>
    /// <para>Ordered black intervals are painted directly. After an invalid transition accepted in
    /// lenient mode, repaint the completed row in its recorded transition order.
    /// Sorting or normalizing these positions would change accepted malformed
    /// input. Strict mode rejects invalid positions and codes.</para>
    /// <para>Input exhaustion discards the incomplete row while keeping earlier rows; a legal Group 4
    /// EOFB or RTC marker also ends decoding when EndOfBlock is enabled. Fixed-height output pads
    /// unfinished rows white. DamagedRowsBeforeError uses EOL resynchronization and replacement
    /// pixels, rebuilding the reference row. The PDF filter stops at corrupt rows in lenient
    /// parsing and throws in strict parsing; it does not invoke historical row repairs.
    /// Fixed-size bitmap overloads retain legacy padding and lenient repairs; the PDF filter
    /// uses variable-length output instead. Arithmetic overflow remains an error in both modes.</para>
    /// <para>Provenance: row/reference-transition and malformed bitmap-write rules are adapted from
    /// the former Apache-2.0 PdfPig decoder, whose C# port attributes
    /// <see href="https://github.com/apache/pdfbox/blob/e644c29279e276bde14ce7a33bdeef0cb1001b3e/pdfbox/src/main/java/org/apache/pdfbox/filter/CCITTFaxDecoderStream.java">this PDFBox decoder</see>.
    /// Signed storage, shared prefix lookups, direct painting and strict/lenient recovery are the
    /// current PdfPig implementation. The signed loop adapts the UInt16 loop while retaining
    /// the signed search sentinel, exact-read failures and recorded-order malformed-row rendering.</para>
    /// <para>Optional EOL, EOFB/RTC and damaged-row substitution follow Adobe PDF Reference 1.7,
    /// section 3.3.5; see
    /// <see href="https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.7old.pdf">PDF Reference</see>.
    /// Reservoir probing, reference rebuilding and bounded unknown-height output are new C# code.</para>
    /// </remarks>
    private struct SignedRowDecoder
    {
        private readonly int columns;
        private readonly CcittFaxCompressionType compressionType;
        private readonly bool encodedByteAlign;
        private readonly bool useLenientParsing;
        private readonly bool acceptOptionalEndOfLine;
        private readonly bool endOfBlock;
        private readonly bool recoverDamagedRows;
        private readonly bool requireEndOfLine;
        private bool pendingEndOfLine;
        // After an invalid transition in a lenient row, stop direct painting. Once the row
        // is decoded, clear it and render all recorded transitions without normalizing positions.
        private bool hasInvalidTransitions;
        private bool referenceHasInvalidTransitions;
        private int[] referenceTransitions;
        private int[] currentTransitions;
        private int referenceTransitionCount;
        private int transitionCount;
        private int lastReferenceTransitionIndex;
        internal SignedRowDecoder(
            int columns,
            CcittFaxCompressionType compressionType,
            bool encodedByteAlign,
            bool useLenientParsing, bool acceptOptionalEndOfLine, bool endOfBlock, bool recoverDamagedRows, bool requireEndOfLine)
        {
            this = default;
            this.columns = columns;
            this.compressionType = compressionType;
            this.encodedByteAlign = encodedByteAlign;
            this.useLenientParsing = useLenientParsing;
            this.acceptOptionalEndOfLine = acceptOptionalEndOfLine;
            this.endOfBlock = endOfBlock;
            this.recoverDamagedRows = recoverDamagedRows;
            this.requireEndOfLine = requireEndOfLine;
            referenceTransitions = new int[columns + 2];
            currentTransitions = new int[columns + 2];
        }

        /// <summary>Checks a row boundary before variable-length decoding starts another row.</summary>
        /// <remarks>This recognizes the actual ending; the outer decoding loop decides whether
        /// EOF is permitted by PDF Rows and parsing policy. pendingEndOfLine is an EOL already
        /// consumed during damaged-row recovery, not evidence of clean row-boundary EOF. Test
        /// markers before starting another row so their bits are not reported as a corrupt run.</remarks>
        internal CcittFaxDecodeStatus ReadEndOfDataStatus(ref CcittFaxCompactBitReader reader,
            bool allowIncompleteRtc = false, bool allowIncompleteEofb = false, bool allowSingleGroup3Eol = false, bool allowZeroFillAtEof = false)
        {
            // Validate EOF padding before alignment can discard unused bits.
            // Arbitrary zero fill requires intact exact positive Rows, checked by the caller.
            // Probe before alignment: otherwise discarded nonzero bits could hide corruption.
            bool cleanEndOfInput = !pendingEndOfLine && (reader.HasOnlyBytePadding
                || allowZeroFillAtEof && reader.TryReadZeroFillToEnd());
            if (encodedByteAlign && !pendingEndOfLine)
            {
                allowIncompleteEofb &= reader.HasZeroAlignmentPadding;
                allowSingleGroup3Eol &= reader.HasZeroAlignmentPadding;
                reader.AlignToByteBoundary();
            }
            if (endOfBlock)
            {
                var status = reader.ReadEndOfBlockStatus(compressionType, pendingEndOfLine, allowIncompleteRtc, allowIncompleteEofb, allowSingleGroup3Eol);
                if (status != CcittFaxDecodeStatus.NotCompleted)
                    return status;
            }
            return cleanEndOfInput
                ? CcittFaxDecodeStatus.EndOfInput : CcittFaxDecodeStatus.NotCompleted;
        }

        internal void DecodeRow<T>(ref CcittFaxCompactBitReader bitReader, Span<byte> rowPixels)
            where T : struct, ICcittRowPolicy
        {
            if (encodedByteAlign && !pendingEndOfLine)
                bitReader.AlignToByteBoundary();
            if (endOfBlock && bitReader.TryReadEndOfBlock(compressionType, pendingEndOfLine))
                throw new EndOfStreamException("CCITT end-of-block marker.");
            bool isOneDimensional;
            if (compressionType == CcittFaxCompressionType.Group4_2D)
            {
                if (acceptOptionalEndOfLine)
                {
                    bool hasEol = bitReader.TryReadEndOfLine();
                    if (requireEndOfLine && !hasEol)
                        throw new CorruptCompressedDataException("Required CCITT EOL is missing.");
                }
                isOneDimensional = false;
            }
            else if (compressionType == CcittFaxCompressionType.ModifiedHuffman)
            {
                if (acceptOptionalEndOfLine)
                    bitReader.TryReadEndOfLine();
                isOneDimensional = true;
            }
            else
            {
                if (!pendingEndOfLine)
                {
                    bool hasEol = bitReader.TryReadEndOfLine();
                    if (requireEndOfLine && !hasEol)
                    {
                        if (!useLenientParsing)
                            throw new CorruptCompressedDataException("Required CCITT EOL is missing.");
                        if (!bitReader.TryResynchronizeAtEndOfLine())
                            throw new EndOfStreamException("Required CCITT EOL not found before data ended.");
                    }
                }
                pendingEndOfLine = false;
                isOneDimensional = compressionType == CcittFaxCompressionType.Group3_1D || bitReader.ReadBitsExact(1) != 0;
            }

            if (!isOneDimensional)
            {
                referenceTransitionCount = transitionCount;
                referenceHasInvalidTransitions = hasInvalidTransitions;
                (referenceTransitions, currentTransitions) = (currentTransitions, referenceTransitions);
            }

            transitionCount = 0;
            hasInvalidTransitions = false;
            rowPixels.Fill(BlackIsOne<T>() ? (byte)0 : (byte)255);
            int pixelPosition = 0;
            bool isWhiteRun = true;
            int whiteReferenceIndex = 0;
            int blackReferenceIndex = 1;
            int nextModeEntry = 0;
            bool hasNextModeEntry = false;
            do
            {
                int operation;
                if (isOneDimensional)
                    operation = HorizontalMode;
                else if (hasNextModeEntry)
                {
                    hasNextModeEntry = false;
                    bitReader.ConsumeBits(nextModeEntry & 7);
                    operation = nextModeEntry >> 3;
                }
                else
                    operation = DecodeMode(ref bitReader);

                if (operation == UnknownMode)
                {
                    if (useLenientParsing)
                        continue;
                    throw new CorruptCompressedDataException("Unknown code in CCITT 2D stream.");
                }

                if (operation == HorizontalMode)
                {
                    if (!hasInvalidTransitions && bitReader.EnsureBits(12))
                    {
                        uint packedRunPair = (isWhiteRun ? RunPairLookup.WhiteFirstPairs : RunPairLookup.BlackFirstPairs)[bitReader.PeekBufferedBits(12)];
                        int firstRunLength = (int)(packedRunPair & 63);
                        int secondRunLength = (int)((packedRunPair >> 6) & 63);
                        if (packedRunPair != 0
                            && firstRunLength + secondRunLength <= columns - pixelPosition
                            && (!isOneDimensional || firstRunLength < columns - pixelPosition))
                        {
                            bitReader.ConsumeBits((int)(packedRunPair >> 12));
                            int firstRunEnd = pixelPosition + firstRunLength;
                            int secondRunEnd = firstRunEnd + secondRunLength;
                            if (!isOneDimensional)
                                PrepareNextMode(ref bitReader, out nextModeEntry, out hasNextModeEntry);
                            // A horizontal pair contains one black interval, irrespective of its starting color.
                            PaintBlackInterval<T>(rowPixels, isWhiteRun ? firstRunEnd : pixelPosition,
                                isWhiteRun ? secondRunEnd : firstRunEnd);
                            currentTransitions[transitionCount++] = firstRunEnd;
                            currentTransitions[transitionCount++] = secondRunEnd;
                            pixelPosition = secondRunEnd;
                            continue;
                        }
                    }

                    int nextPosition = AddRunLength(pixelPosition, DecodeRunLength(ref bitReader, isWhiteRun));
                    if (isOneDimensional)
                    {
                        PaintDecodedRun<T>(rowPixels, pixelPosition, nextPosition, isWhiteRun);
                        currentTransitions[transitionCount++] = nextPosition;
                        pixelPosition = nextPosition;
                        isWhiteRun = !isWhiteRun;
                    }
                    else
                    {
                        currentTransitions[transitionCount++] = nextPosition;
                        int secondRunEnd = AddRunLength(nextPosition, DecodeRunLength(ref bitReader, !isWhiteRun));
                        PrepareNextMode(ref bitReader, out nextModeEntry, out hasNextModeEntry);
                        if (!hasInvalidTransitions)
                            PaintBlackInterval<T>(rowPixels, isWhiteRun ? nextPosition : pixelPosition,
                                isWhiteRun ? secondRunEnd : nextPosition);
                        currentTransitions[transitionCount++] = secondRunEnd;
                        pixelPosition = secondRunEnd;
                    }
                }
                else
                {
                    int referenceTransitionIndex;
                    if (!hasInvalidTransitions && !referenceHasInvalidTransitions)
                    {
                        // Ordered rows use the same monotonically advancing color cursors as compact decoding.
                        referenceTransitionIndex = isWhiteRun ? whiteReferenceIndex : blackReferenceIndex;
                        while (referenceTransitionIndex < referenceTransitionCount && pixelPosition != 0
                            && referenceTransitions[referenceTransitionIndex] <= pixelPosition)
                            referenceTransitionIndex += 2;
                        if (isWhiteRun)
                            whiteReferenceIndex = referenceTransitionIndex;
                        else
                            blackReferenceIndex = referenceTransitionIndex;
                        if (pixelPosition != 0 && referenceTransitionIndex >= referenceTransitionCount)
                            referenceTransitionIndex = -1;
                        else if (pixelPosition != 0)
                            lastReferenceTransitionIndex = referenceTransitionIndex;
                    }
                    else
                        referenceTransitionIndex = FindReferenceTransition(pixelPosition, isWhiteRun);

                    int nextPosition;
                    if (operation == PassMode)
                    {
                        // Preserve the signed search's -1 sentinel: a missing b1 wraps pass to entry zero.
                        referenceTransitionIndex++;
                        nextPosition = referenceTransitionIndex >= referenceTransitionCount ? columns : referenceTransitions[referenceTransitionIndex];
                    }
                    else
                        nextPosition = checked((referenceTransitionIndex >= referenceTransitionCount || referenceTransitionIndex == -1
                            ? columns : referenceTransitions[referenceTransitionIndex]) + operation);

                    ValidateTransitionPosition(pixelPosition, nextPosition);
                    PrepareNextMode(ref bitReader, out nextModeEntry, out hasNextModeEntry);
                    PaintDecodedRun<T>(rowPixels, pixelPosition, nextPosition, isWhiteRun);
                    pixelPosition = nextPosition;
                    if (operation != PassMode)
                    {
                        currentTransitions[transitionCount++] = pixelPosition;
                        isWhiteRun = !isWhiteRun;
                    }
                }
            }
            while (pixelPosition < columns);
            lastReferenceTransitionIndex = 0;
            if (hasInvalidTransitions)
            {
                rowPixels.Fill(BlackIsOne<T>() ? (byte)0 : (byte)255);
                RenderMalformedRow(rowPixels, BlackIsOne<T>());
            }
        }

        /// <summary>Reuses a premature EOL already read, or searches for the next row boundary.</summary>
        internal bool TryResynchronize(ref CcittFaxCompactBitReader reader) =>
            pendingEndOfLine || reader.TryResynchronizeAtEndOfLine();

        /// <summary>Rebuilds the next reference row from replacement pixels after EOL resynchronization.</summary>
        /// <remarks>DamagedRowsBeforeError substitutes the previous undamaged row or white. Its
        /// transitions must replace the failed row's partial state before the following 2D row.</remarks>
        internal void UseReplacementRow(ReadOnlySpan<byte> pixels, bool blackIsOne)
        {
            transitionCount = 0;
            bool previousIsBlack = false;
            for (int x = 0; x < columns; x++)
            {
                bool isBlack = ((pixels[x >> 3] >> (7 - (x & 7))) & 1) == (blackIsOne ? 1 : 0);
                if (isBlack != previousIsBlack)
                    currentTransitions[transitionCount++] = x;
                previousIsBlack = isBlack;
            }
            currentTransitions[transitionCount++] = columns;
            hasInvalidTransitions = false;
            lastReferenceTransitionIndex = 0;
            pendingEndOfLine = true;
        }

        // Peek only a complete ordinary mode. Short tails and unmapped prefixes retain exact-read recovery.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PrepareNextMode(ref CcittFaxCompactBitReader bitReader, out int entry, out bool available)
        {
            entry = bitReader.EnsureBits(7) ? ModeLookup[bitReader.PeekBufferedBits(7)] : 0;
            available = entry != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PaintDecodedRun<T>(Span<byte> rowPixels, int startPosition, int endPosition, bool isWhiteRun)
            where T : struct, ICcittRowPolicy
        {
            if (!hasInvalidTransitions && !isWhiteRun && endPosition > startPosition)
                PaintBlackInterval<T>(rowPixels, startPosition, endPosition);
        }

        private int AddRunLength(int position, int runLength)
        {
            int nextPosition = checked(position + runLength);
            ValidateTransitionPosition(position, nextPosition);
            return nextPosition;
        }

        private void ValidateTransitionPosition(int previousPosition, int nextPosition)
        {
            if (nextPosition < previousPosition || nextPosition < 0 || nextPosition > columns)
            {
                if (!useLenientParsing)
                    throw new CorruptCompressedDataException($"Invalid CCITT changing position: {nextPosition}, previous={previousPosition}, columns={columns}.");
                hasInvalidTransitions = true;
            }
        }

        // Even reference entries change white to black;
        // odd entries change black to white. Search the parity needed by the current run color,
        // retaining a zero-position transition at the start of a row.
        private int FindReferenceTransition(int pixelPosition, bool isWhiteRun)
        {
            int searchStartIndex = (lastReferenceTransitionIndex & ~1) + (isWhiteRun ? 0 : 1);
            if (searchStartIndex > 2)
                searchStartIndex -= 2;
            if (pixelPosition == 0)
                return searchStartIndex;
            for (int referenceIndex = searchStartIndex; referenceIndex < referenceTransitionCount; referenceIndex += 2)
                if (pixelPosition < referenceTransitions[referenceIndex])
                {
                    lastReferenceTransitionIndex = referenceIndex;
                    return referenceIndex;
                }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeRunLength(ref CcittFaxCompactBitReader bitReader, bool isWhiteRun)
        {
            var lookup = isWhiteRun ? WhiteRunLookup : BlackRunLookup;
            int lookaheadBitCount = isWhiteRun ? 12 : 13;
            int accumulatedRunLength = 0;
            int runOrEolMarker;
            do
            {
                int lookupEntry = bitReader.EnsureBits(lookaheadBitCount) ? lookup[bitReader.PeekBufferedBits(lookaheadBitCount)] : 0;
                if (lookupEntry != 0)
                {
                    bitReader.ConsumeBits(lookupEntry & 15);
                    runOrEolMarker = lookupEntry >> 4;
                }
                else
                    runOrEolMarker = DecodeRunBitByBit(ref bitReader, isWhiteRun ? CcittFaxCodebook.WhiteRunCodes : CcittFaxCodebook.BlackRunCodes);
                if (runOrEolMarker < 0 && (recoverDamagedRows || !useLenientParsing))
                {
                    // A premature EOL already gives the resynchronization point. Retain it
                    // for the next row rather than skipping another complete row during recovery.
                    pendingEndOfLine = true;
                    throw new CorruptCompressedDataException("Premature EOL in CCITT run.");
                }
                accumulatedRunLength = checked(accumulatedRunLength + runOrEolMarker);
                if (runOrEolMarker >= 0)
                    ValidateTransitionPosition(0, accumulatedRunLength);
            }
            while (runOrEolMarker >= 64);
            return runOrEolMarker >= 0 ? accumulatedRunLength : columns;
        }

        private static int DecodeRunBitByBit(ref CcittFaxCompactBitReader bitReader, CcittCode[] runCodes)
        {
            int codePrefix = 0;
            for (int codeBitCount = 1; codeBitCount <= 13; codeBitCount++)
            {
                codePrefix = (codePrefix << 1) | bitReader.ReadBitsExact(1);
                if (codeBitCount == 12 && codePrefix == 1)
                    // EOL encountered within a run is treated as a full-width run.
                    return EndOfLineRunMarker;
                if (codeBitCount == 12 && codePrefix == 0)
                {
                    while (bitReader.ReadBitsExact(1) == 0)
                    {
                    }

                    return EndOfLineRunMarker;
                }

                bool isValidPrefix = codePrefix == 0 && codeBitCount < 12;
                foreach (var code in runCodes)
                {
                    if (code.Length < codeBitCount || (code.Bits >> (code.Length - codeBitCount)) != codePrefix)
                        continue;
                    if (code.Length == codeBitCount)
                        return code.Run;
                    isValidPrefix = true;
                }

                if (!isValidPrefix)
                    throw new CorruptCompressedDataException("Unknown code in Huffman RLE stream");
            }

            throw new CorruptCompressedDataException("Unknown code in Huffman RLE stream");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeMode(ref CcittFaxCompactBitReader bitReader)
        {
            if (bitReader.EnsureBits(7))
            {
                int lookupEntry = ModeLookup[bitReader.PeekBufferedBits(7)];
                if (lookupEntry != 0)
                {
                    bitReader.ConsumeBits(lookupEntry & 7);
                    return lookupEntry >> 3;
                }
            }

            // EOL cannot be an ordinary mode prefix. Probe it only after the fast lookup
            // misses, before consuming any of its bits during damaged-row recovery.
            if (recoverDamagedRows && bitReader.TryReadEndOfLine())
            {
                pendingEndOfLine = true;
                throw new CorruptCompressedDataException("Premature EOL in CCITT 2D row.");
            }
            // Read unmapped prefixes one bit at a time. Complete row-boundary end markers
            // were handled before decoding; six zero bits here indicate an invalid mode.
            if (bitReader.ReadBitsExact(1) != 0)
                return 0;
            int middleBits = bitReader.ReadBitsExact(2);
            if (middleBits == 1)
                return HorizontalMode;
            if (middleBits >= 2)
                return middleBits == 3 ? 1 : -1;
            if (bitReader.ReadBitsExact(1) != 0)
                return PassMode;
            int tailBits = bitReader.ReadBitsExact(2);
            if (tailBits >= 2)
                return tailBits == 3 ? 2 : -2;
            if (tailBits == 1)
                return bitReader.ReadBitsExact(1) != 0 ? 3 : -3;
            return UnknownMode;
        }

        // Repaint transitions in their recorded order. Sorting, merging or extra clipping
        // would change the pixels produced for invalid positions in lenient mode.
        private void RenderMalformedRow(Span<byte> rowPixels, bool blackIsOne)
        {
            var pixelPosition = 0;
            var isWhiteRun = true;
            lastReferenceTransitionIndex = 0;
            for (var transitionIndex = 0; transitionIndex <= transitionCount; transitionIndex++)
            {
                var nextTransitionPosition = columns;
                if (transitionIndex != transitionCount)
                {
                    nextTransitionPosition = currentTransitions[transitionIndex];
                }

                ValidateTransitionPosition(pixelPosition, nextTransitionPosition);
                if (nextTransitionPosition > columns)
                {
                    nextTransitionPosition = columns;
                }

                if (pixelPosition >= 0 && nextTransitionPosition >= pixelPosition)
                {
                    var byteIndex = pixelPosition / 8;
                    var bitOffset = pixelPosition & 7;
                    if (bitOffset != 0)
                    {
                        var pixelCount = Math.Min(8 - bitOffset, nextTransitionPosition - pixelPosition);
                        var pixelMask = (byte)((255 >> bitOffset) & (255 << (8 - bitOffset - pixelCount)));
                        if (blackIsOne)
                            rowPixels[byteIndex] |= (byte)(isWhiteRun ? 0 : pixelMask);
                        else
                            rowPixels[byteIndex] &= (byte)~(isWhiteRun ? 0 : pixelMask);
                        pixelPosition += pixelCount;
                        if ((pixelPosition & 7) == 0)
                            byteIndex++;
                    }

                    var byteCount = (nextTransitionPosition - pixelPosition) / 8;
                    if (byteCount != 0)
                    {
                        rowPixels.Slice(byteIndex, byteCount).Fill((byte)(isWhiteRun == blackIsOne ? 0 : 255));
                        pixelPosition += byteCount * 8;
                        byteIndex += byteCount;
                    }

                    if (nextTransitionPosition > pixelPosition)
                    {
                        var pixelMask = (byte)(255 << (8 - (nextTransitionPosition - pixelPosition)));
                        rowPixels[byteIndex] = blackIsOne ? (byte)(isWhiteRun ? 0 : pixelMask) : (byte)~(isWhiteRun ? 0 : pixelMask);
                        pixelPosition = nextTransitionPosition;
                    }
                }
                else
                {
                    // Negative or backwards positions cannot use the interval painter,
                    // which requires ordered, nonnegative bounds.
                    var byteIndex = pixelPosition / 8;
                    while (pixelPosition % 8 != 0 && nextTransitionPosition - pixelPosition > 0)
                    {
                        var pixelMask = (byte)(isWhiteRun ? 0 : 1 << 7 - pixelPosition % 8);
                        if (blackIsOne)
                            rowPixels[byteIndex] |= pixelMask;
                        else
                            rowPixels[byteIndex] &= (byte)~pixelMask;
                        pixelPosition++;
                    }

                    if (pixelPosition % 8 == 0)
                    {
                        byteIndex = pixelPosition / 8;
                        var runOrEolMarker = (byte)(isWhiteRun == blackIsOne ? 0x00 : 0xff);
                        if (nextTransitionPosition - pixelPosition > 7)
                        {
                            var byteCount = (nextTransitionPosition - pixelPosition) / 8;
                            rowPixels.Slice(byteIndex, byteCount).Fill(runOrEolMarker);
                            pixelPosition += byteCount * 8;
                            byteIndex += byteCount;
                        }
                    }

                    while (nextTransitionPosition - pixelPosition > 0)
                    {
                        if (pixelPosition % 8 == 0)
                        {
                            rowPixels[byteIndex] = blackIsOne ? (byte)0 : (byte)255;
                        }

                        var pixelMask = (byte)(isWhiteRun ? 0 : 1 << 7 - pixelPosition % 8);
                        if (blackIsOne)
                            rowPixels[byteIndex] |= pixelMask;
                        else
                            rowPixels[byteIndex] &= (byte)~pixelMask;
                        pixelPosition++;
                    }
                }

                isWhiteRun = !isWhiteRun;
            }

            if (pixelPosition != columns)
            {
                throw new CorruptCompressedDataException($"Sum of run-lengths does not equal scan line width: {pixelPosition} > {columns}");
            }
        }
    }

    /// <summary>Decodes the bitmap, retrying the original input through signed decoding when needed.</summary>
    /// <remarks>Dimensions, output capacity and the allocation budget must already be validated.
    /// A retry rebuilds every reference row; it does not resume from a partially decoded row.</remarks>
    internal static void Decode(
        ReadOnlySpan<byte> compressedInput,
        byte[] decodedBitmap,
        int columns,
        int rowCount,
        CcittFaxCompressionType compressionType,
        bool encodedByteAlign,
        bool blackIsOne,
        bool useLenientParsing,
        bool acceptOptionalEndOfLine = false,
        bool endOfBlock = true,
        int damagedRowsBeforeError = 0,
        bool requireEndOfLine = true)
    {
        if (TryDecode(compressedInput, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, blackIsOne, acceptOptionalEndOfLine, endOfBlock, requireEndOfLine))
            return;
        // Rebuilding signed reference rows avoids per-row checkpoints on the compact fast path.
        DecodeCompatibility(compressedInput, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, blackIsOne, useLenientParsing, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError, requireEndOfLine);
    }

    /// <summary>Decodes all requested rows through the signed-position recovery path.</summary>
    /// <remarks>Uses the same codewords, lookup tables, bit reader and interval painter as compact decoding.</remarks>
    internal static void DecodeCompatibility(
        ReadOnlySpan<byte> compressedInput,
        byte[] decodedBitmap,
        int columns,
        int rowCount,
        CcittFaxCompressionType compressionType,
        bool encodedByteAlign,
        bool blackIsOne,
        bool useLenientParsing,
        bool acceptOptionalEndOfLine = false,
        bool endOfBlock = true,
        int damagedRowsBeforeError = 0,
        bool requireEndOfLine = true)
    {
        var bitReader = new CcittFaxCompactBitReader(compressedInput);
        if (compressionType == CcittFaxCompressionType.Group4_2D)
        {
            if (blackIsOne)
                DecodeSignedRows<Group4BlackIsOnePolicy>(ref bitReader, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, useLenientParsing, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError, requireEndOfLine);
            else
                DecodeSignedRows<Group4BlackIsZeroPolicy>(ref bitReader, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, useLenientParsing, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError, requireEndOfLine);
        }
        else if (blackIsOne)
            DecodeSignedRows<GeneralBlackIsOnePolicy>(ref bitReader, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, useLenientParsing, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError, requireEndOfLine);
        else
            DecodeSignedRows<GeneralBlackIsZeroPolicy>(ref bitReader, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, useLenientParsing, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError, requireEndOfLine);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BlackIsOne<T>()
        where T : struct, ICcittRowPolicy
    {
#if NET8_0_OR_GREATER
        return T.BlackIsOne;
#else
        return default(T).BlackIsOne;
#endif
    }

    private static void DecodeSignedRows<T>(
        ref CcittFaxCompactBitReader bitReader,
        byte[] decodedBitmap,
        int columns,
        int rowCount,
        CcittFaxCompressionType compressionType,
        bool encodedByteAlign,
        bool useLenientParsing,
        bool acceptOptionalEndOfLine,
        bool endOfBlock,
        int damagedRowsBeforeError,
        bool requireEndOfLine)
        where T : struct, ICcittRowPolicy
    {
        int rowByteCount = (columns + 7) / 8;
        var rowDecoder = new SignedRowDecoder(columns, compressionType, encodedByteAlign,
            useLenientParsing && damagedRowsBeforeError == 0, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError > 0, requireEndOfLine);
        int damagedRowCount = 0;
        bool previousRowWasDamaged = false;
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            try
            {
                rowDecoder.DecodeRow<T>(ref bitReader, decodedBitmap.AsSpan(rowIndex * rowByteCount, rowByteCount));
                previousRowWasDamaged = false;
            }
            catch (Exception exception) when (damagedRowsBeforeError > 0
                && (exception is CorruptCompressedDataException || exception is IndexOutOfRangeException))
            {
                RecoverDamagedRow(ref rowDecoder, ref bitReader,
                    decodedBitmap.AsSpan(rowIndex * rowByteCount, rowByteCount),
                    rowIndex == 0 ? ReadOnlySpan<byte>.Empty : decodedBitmap.AsSpan((rowIndex - 1) * rowByteCount, rowByteCount),
                    BlackIsOne<T>(), previousRowWasDamaged, ref damagedRowCount, damagedRowsBeforeError);
                previousRowWasDamaged = true;
            }
            catch (IndexOutOfRangeException exception)
            {
                throw new CorruptCompressedDataException("Malformed CCITT stream: decoder buffer bounds exceeded.", exception);
            }
            catch (OverflowException exception)
            {
                throw new CorruptCompressedDataException("Malformed CCITT stream: run arithmetic overflow.", exception);
            }
            // Exhausted input or a legal EOFB ends decoding in both parsing modes. Corrupt
            // codes end decoding this way only in lenient mode. Keep earlier complete rows;
            // discard the current row and fill it and the remaining rows white, including padding.
            // Arithmetic overflow always propagates. Bounds failures can be resynchronized only
            // when an explicit damaged-row budget is active.
            catch (EndOfStreamException)
            {
                decodedBitmap.AsSpan(rowIndex * rowByteCount).Fill(BlackIsOne<T>() ? (byte)0 : (byte)255);
                return;
            }
            catch (CorruptCompressedDataException exception) when (useLenientParsing && exception.InnerException is not IndexOutOfRangeException && exception.InnerException is not OverflowException)
            {
                decodedBitmap.AsSpan(rowIndex * rowByteCount).Fill(BlackIsOne<T>() ? (byte)0 : (byte)255);
                return;
            }
        }
    }
    private static void DecodeSignedRow<T>(ref SignedRowDecoder decoder,
        ref CcittFaxCompactBitReader reader, Span<byte> pixels) where T : struct, ICcittRowPolicy
    {
        try
        {
            decoder.DecodeRow<T>(ref reader, pixels);
        }
        catch (IndexOutOfRangeException exception)
        {
            throw new CorruptCompressedDataException("Malformed CCITT stream: decoder buffer bounds exceeded.", exception);
        }
        catch (OverflowException exception)
        {
            throw new CorruptCompressedDataException("Malformed CCITT stream: run arithmetic overflow.", exception);
        }
    }

    private static void RecoverDamagedRow(ref SignedRowDecoder decoder, ref CcittFaxCompactBitReader reader,
        Span<byte> pixels, ReadOnlySpan<byte> previousPixels, bool blackIsOne, bool previousRowWasDamaged,
        ref int damagedRowCount, int damagedRowsBeforeError)
    {
        if (++damagedRowCount > damagedRowsBeforeError)
            throw new CorruptCompressedDataException("CCITT damaged-row budget exceeded.");
        if (!decoder.TryResynchronize(ref reader))
            throw new CorruptCompressedDataException("Cannot resynchronize damaged CCITT row at EOL.");
        if (!previousPixels.IsEmpty && !previousRowWasDamaged)
            previousPixels.CopyTo(pixels);
        else
            pixels.Fill(blackIsOne ? (byte)0 : (byte)255);
        decoder.UseReplacementRow(pixels, blackIsOne);
    }

    /// <summary>Returns complete rows up to the PDF stopping condition without synthesizing missing rows.</summary>
    /// <remarks>maximumRows = 0 permits unknown height; otherwise it bounds the number of decoded rows.
    /// Strict parsing rejects incomplete rows and missing required markers unless an enabled, validated
    /// termination exception applies. Exact declared rows must not include substituted damaged rows. MemoryStream capacity is bounded;
    /// resizing accounts for both old and new arrays, the row buffer and signed transitions.
    /// Return the used portion of its owned buffer without a second full-size copy.</remarks>
    internal static Memory<byte> DecodeRowsToMemory(ReadOnlySpan<byte> input, int columns,
        CcittFaxCompressionType compressionType, bool encodedByteAlign, bool blackIsOne,
        bool useLenientParsing, bool acceptOptionalEndOfLine, bool endOfBlock, int damagedRowsBeforeError,
        long maximumDecodeBufferBytes = CcittFaxDecodeFilter.MaximumDecodeBufferBytes,
        bool requireEndOfLine = true, int maximumRows = 0, bool requireEndMarker = false, bool allowEndTolerance = false,
        int expectedRowsForEndTolerance = 0)
        => DecodeRowsToMemory(input, columns, compressionType, encodedByteAlign, blackIsOne,
            useLenientParsing, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError,
            out _, out _, maximumDecodeBufferBytes, requireEndOfLine, maximumRows, requireEndMarker, allowEndTolerance, expectedRowsForEndTolerance);

    /// <summary>Returns pixels, byte-rounded consumption and the stopping condition from the signed path.</summary>
    /// <remarks>maximumStoredRows bounds image output without changing the decoded row count used
    /// for termination. Excess rows are still validated in the reusable row buffer. NotCompleted
    /// offsets describe the failed decode, not an accepted image boundary.</remarks>
    internal static Memory<byte> DecodeRowsToMemory(ReadOnlySpan<byte> input, int columns,
        CcittFaxCompressionType compressionType, bool encodedByteAlign, bool blackIsOne,
        bool useLenientParsing, bool acceptOptionalEndOfLine, bool endOfBlock, int damagedRowsBeforeError,
        out int bytesConsumed, out CcittFaxDecodeStatus status,
        long maximumDecodeBufferBytes = CcittFaxDecodeFilter.MaximumDecodeBufferBytes,
        bool requireEndOfLine = true, int maximumRows = 0, bool requireEndMarker = false, bool allowEndTolerance = false,
        int expectedRowsForEndTolerance = 0, int maximumStoredRows = 0)
    {
        var reader = new CcittFaxCompactBitReader(input);
        return blackIsOne
            ? DecodeRowsToMemory<GeneralBlackIsOnePolicy>(ref reader, columns, compressionType, encodedByteAlign,
                useLenientParsing, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError, maximumDecodeBufferBytes, requireEndOfLine, maximumRows, requireEndMarker, allowEndTolerance, expectedRowsForEndTolerance, maximumStoredRows, out bytesConsumed, out status)
            : DecodeRowsToMemory<GeneralBlackIsZeroPolicy>(ref reader, columns, compressionType, encodedByteAlign,
                useLenientParsing, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError, maximumDecodeBufferBytes, requireEndOfLine, maximumRows, requireEndMarker, allowEndTolerance, expectedRowsForEndTolerance, maximumStoredRows, out bytesConsumed, out status);
    }

    private static Memory<byte> DecodeRowsToMemory<T>(ref CcittFaxCompactBitReader reader, int columns,
        CcittFaxCompressionType compressionType, bool encodedByteAlign, bool useLenientParsing,
        bool acceptOptionalEndOfLine, bool endOfBlock, int damagedRowsBeforeError, long maximumDecodeBufferBytes, bool requireEndOfLine, int maximumRows, bool requireEndMarker, bool allowEndTolerance, int expectedRowsForEndTolerance, int maximumStoredRows,
        out int bytesConsumed, out CcittFaxDecodeStatus status) where T : struct, ICcittRowPolicy
    {
        bytesConsumed = 0;
        status = CcittFaxDecodeStatus.RowLimit;
        int rowByteCount = (columns + 7) / 8;
        // Explicit damaged-row recovery needs the preceding decoded row even after image
        // storage has stopped. Keep it separately rather than referring to the last stored row.
        long workingBytes = rowByteCount + 2L * (columns + 2) * sizeof(int)
            + (damagedRowsBeforeError > 0 ? rowByteCount : 0);
        if (workingBytes > maximumDecodeBufferBytes)
            throw new CorruptCompressedDataException("CCITT growing decode buffers exceed the allocation budget.");
        var decoder = new SignedRowDecoder(columns, compressionType, encodedByteAlign,
            // PDF rows are never repaired implicitly. Lenient parsing may stop at a failed row;
            // only the explicit damage budget permits resynchronization and substitution.
            false, acceptOptionalEndOfLine, endOfBlock, damagedRowsBeforeError > 0, requireEndOfLine);
        var rowPixels = new byte[rowByteCount];
        using var output = new MemoryStream();
        int decodedRowCount = 0;
        var previousRowPixels = damagedRowsBeforeError > 0 ? new byte[rowByteCount] : null;
        int damagedRowCount = 0;
        bool previousRowWasDamaged = false;
        while (maximumRows == 0 || decodedRowCount < maximumRows)
        {
            // Stored output length does not count discarded rows. A substituted damaged row
            // also counts as a decoded row but cannot authorize a shortened ending. Count all repairs, even if later rows decoded
            // successfully, before allowing declared-Rows EOF or shortened termination.
            bool exactDeclaredRows = expectedRowsForEndTolerance > 0 && damagedRowCount == 0
                && decodedRowCount == expectedRowsForEndTolerance;
            var endStatus = decoder.ReadEndOfDataStatus(ref reader, allowEndTolerance && decodedRowCount > 0
                && (expectedRowsForEndTolerance == 0 || exactDeclaredRows),
                allowIncompleteEofb: allowEndTolerance && exactDeclaredRows,
                allowSingleGroup3Eol: allowEndTolerance && exactDeclaredRows,
                allowZeroFillAtEof: allowEndTolerance && exactDeclaredRows);
            if (endStatus != CcittFaxDecodeStatus.NotCompleted)
            {
                status = endStatus;
                // A full marker always wins over Rows. IncompleteEndOfBlock was already
                // authorized by the guarded probe; clean EOF needs its exact-Rows check here.
                // Lenient rejection retains the decoded prefix, but marks it NotCompleted.
                if (requireEndMarker && status != CcittFaxDecodeStatus.EndOfBlock
                    && status != CcittFaxDecodeStatus.IncompleteEndOfBlock
                    && !(status == CcittFaxDecodeStatus.EndOfInput && allowEndTolerance && exactDeclaredRows))
                {
                    if (!useLenientParsing)
                        throw new CorruptCompressedDataException("Required CCITT end-of-block marker is missing.");
                    status = CcittFaxDecodeStatus.NotCompleted;
                }
                break;
            }
            if (maximumStoredRows > 0 && (long)(decodedRowCount + 1) * rowByteCount > maximumDecodeBufferBytes)
                throw new CorruptCompressedDataException("CCITT decoded rows exceed the work budget.");
            try
            {
                DecodeSignedRow<T>(ref decoder, ref reader, rowPixels);
                previousRowWasDamaged = false;
            }
            catch (EndOfStreamException exception)
            {
                // Row-boundary exhaustion was handled above; EOF here interrupts a started row.
                if (!useLenientParsing)
                    throw new CorruptCompressedDataException("Truncated CCITT row.", exception);
                status = CcittFaxDecodeStatus.NotCompleted;
                break;
            }
            catch (CorruptCompressedDataException exception) when (damagedRowsBeforeError > 0 && exception.InnerException is not OverflowException)
            {
                RecoverDamagedRow(ref decoder, ref reader, rowPixels,
                    decodedRowCount == 0 ? ReadOnlySpan<byte>.Empty : previousRowPixels.AsSpan(),
                    BlackIsOne<T>(), previousRowWasDamaged, ref damagedRowCount, damagedRowsBeforeError);
                previousRowWasDamaged = true;
            }
            catch (CorruptCompressedDataException exception) when (useLenientParsing && exception.InnerException is not IndexOutOfRangeException && exception.InnerException is not OverflowException)
            {
                status = CcittFaxDecodeStatus.NotCompleted;
                break;
            }
            if (maximumStoredRows == 0 || decodedRowCount < maximumStoredRows)
            {
                EnsureUnknownRowCapacity(output, rowByteCount, workingBytes, maximumDecodeBufferBytes);
                output.Write(rowPixels, 0, rowByteCount);
            }
            if (previousRowPixels is not null)
                rowPixels.AsSpan().CopyTo(previousRowPixels);
            decodedRowCount++;
        }
        if (status != CcittFaxDecodeStatus.NotCompleted)
            reader.AlignToByteBoundary();
        bytesConsumed = reader.BytesConsumed;
        return output.Length == 0 ? Memory<byte>.Empty : output.GetBuffer().AsMemory(0, (int)output.Length);
    }

    private static void EnsureUnknownRowCapacity(MemoryStream output, int rowByteCount, long workingBytes, long maximumDecodeBufferBytes)
    {
        long requiredCapacity = output.Length + rowByteCount;
        if (requiredCapacity <= output.Capacity)
            return;
        long availableBytes = maximumDecodeBufferBytes - workingBytes - output.Capacity;
        long newCapacity = Math.Min(Math.Max(requiredCapacity, Math.Max(4096L, 2L * output.Capacity)), availableBytes);
        if (newCapacity < requiredCapacity)
            throw new CorruptCompressedDataException("CCITT growing decode buffers exceed the allocation budget.");
        output.Capacity = (int)newCapacity;
    }

}
