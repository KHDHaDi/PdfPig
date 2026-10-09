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
    /// end-of-facsimile-block marker also ends decoding. The outer row loop pads unfinished output
    /// with white. Lenient mode additionally recovers from certain compressed-data errors; buffer
    /// bounds failures and arithmetic overflow remain errors in both modes.</para>
    /// <para>Provenance: row/reference-transition and malformed bitmap-write rules are adapted from
    /// the former Apache-2.0 PdfPig decoder, whose C# port attributes
    /// <see href="https://github.com/apache/pdfbox/blob/e644c29279e276bde14ce7a33bdeef0cb1001b3e/pdfbox/src/main/java/org/apache/pdfbox/filter/CCITTFaxDecoderStream.java">this PDFBox decoder</see>.
    /// Signed storage, shared prefix lookups, direct painting and strict/lenient recovery are the
    /// current PdfPig implementation. The signed loop adapts the UInt16 loop while retaining
    /// the signed search sentinel, exact-read failures and recorded-order malformed-row rendering.</para>
    /// </remarks>
    private struct SignedRowDecoder
    {
        private readonly int columns;
        private readonly CcittFaxCompressionType compressionType;
        private readonly bool encodedByteAlign;
        private readonly bool useLenientParsing;
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
            bool useLenientParsing)
        {
            this = default;
            this.columns = columns;
            this.compressionType = compressionType;
            this.encodedByteAlign = encodedByteAlign;
            this.useLenientParsing = useLenientParsing;
            referenceTransitions = new int[columns + 2];
            currentTransitions = new int[columns + 2];
        }

        internal void DecodeRow<T>(ref CcittFaxCompactBitReader bitReader, Span<byte> rowPixels)
            where T : struct, ICcittRowPolicy
        {
            if (encodedByteAlign)
                bitReader.AlignToByteBoundary();
            bool isOneDimensional;
#if NET8_0_OR_GREATER
            if (T.IsGroup4)
#else
            if (default(T).IsGroup4)
#endif
                isOneDimensional = false;
            else
            {
                switch (compressionType)
                {
                    case CcittFaxCompressionType.ModifiedHuffman:
                        isOneDimensional = true;
                        break;
                    case CcittFaxCompressionType.Group4_2D:
                        isOneDimensional = false;
                        break;
                    case CcittFaxCompressionType.Group3_1D:
                    case CcittFaxCompressionType.Group3_2D:
                        int precedingZeroCount = 0;
                        while (true)
                        {
                            if (bitReader.ReadBitsExact(1) == 0)
                            {
                                // EOL needs at least eleven zeros before a one. Saturate at eleven
                                // to accept longer fill sequences without overflowing the counter.
                                if (precedingZeroCount < 11)
                                    precedingZeroCount++;
                            }
                            else if (precedingZeroCount >= 11)
                                break;
                            else
                                precedingZeroCount = 0;
                        }

                        isOneDimensional = compressionType == CcittFaxCompressionType.Group3_1D || bitReader.ReadBitsExact(1) != 0;
                        break;
                    default:
                        throw new InvalidOperationException(compressionType + " is not a supported compression type.");
                }
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
                    if (compressionType == CcittFaxCompressionType.Group4_2D
                        && pixelPosition == 0
                        && transitionCount == 0
                        && bitReader.ReadBitsExact(6) == 1
                        && bitReader.ReadBitsExact(12) == 1)
                        throw new EndOfStreamException("CCITT end-of-facsimile block.");
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
        private static int DecodeMode(ref CcittFaxCompactBitReader bitReader)
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

            // Read unmapped prefixes one bit at a time. Six zero bits return the sentinel
            // checked by DecodeRow for a legal Group 4 EOFB or a strict-mode error.
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
        bool useLenientParsing)
    {
        if (TryDecode(compressedInput, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, blackIsOne))
            return;
        // Rebuilding signed reference rows avoids per-row checkpoints on the compact fast path.
        DecodeCompatibility(compressedInput, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, blackIsOne, useLenientParsing);
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
        bool useLenientParsing)
    {
        var bitReader = new CcittFaxCompactBitReader(compressedInput);
        if (compressionType == CcittFaxCompressionType.Group4_2D)
        {
            if (blackIsOne)
                DecodeSignedRows<Group4BlackIsOnePolicy>(ref bitReader, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, useLenientParsing);
            else
                DecodeSignedRows<Group4BlackIsZeroPolicy>(ref bitReader, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, useLenientParsing);
        }
        else if (blackIsOne)
            DecodeSignedRows<GeneralBlackIsOnePolicy>(ref bitReader, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, useLenientParsing);
        else
            DecodeSignedRows<GeneralBlackIsZeroPolicy>(ref bitReader, decodedBitmap, columns, rowCount, compressionType, encodedByteAlign, useLenientParsing);
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
        bool useLenientParsing)
        where T : struct, ICcittRowPolicy
    {
        int rowByteCount = (columns + 7) / 8;
        var rowDecoder = new SignedRowDecoder(columns, compressionType, encodedByteAlign, useLenientParsing);
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            try
            {
                rowDecoder.DecodeRow<T>(ref bitReader, decodedBitmap.AsSpan(rowIndex * rowByteCount, rowByteCount));
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
            // Bounds failures and arithmetic overflow above always propagate as corruption.
            catch (EndOfStreamException)
            {
                decodedBitmap.AsSpan(rowIndex * rowByteCount).Fill(BlackIsOne<T>() ? (byte)0 : (byte)255);
                return;
            }
            catch (CorruptCompressedDataException) when (useLenientParsing)
            {
                decodedBitmap.AsSpan(rowIndex * rowByteCount).Fill(BlackIsOne<T>() ? (byte)0 : (byte)255);
                return;
            }
        }
    }
}
