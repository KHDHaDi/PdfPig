using System;
using System.IO;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace UglyToad.PdfPig.Filters.CcittFax;
/// <summary>Supplies constant coding-family and output-polarity choices to generic decoder methods.</summary>
/// <remarks>
/// Value-type policies let the JIT specialize Group 4 framing and BlackIs1 painting.
/// .NET 8 and later use static interface properties; older targets use constrained instance
/// properties on a default struct without boxing.
/// </remarks>
internal interface ICcittRowPolicy
{
#if NET8_0_OR_GREATER
    static abstract bool IsGroup4 { get; }

    static abstract bool BlackIsOne { get; }

#else
    bool IsGroup4 { get; }

    bool BlackIsOne { get; }

#endif
}

/// <summary>Identifies why CCITT decoding stopped; consumption alone does not establish a valid image boundary.</summary>
internal enum CcittFaxDecodeStatus
{
    /// <summary>Invalid input or parameters stopped decoding; the offset is diagnostic only.</summary>
    NotCompleted,
    /// <summary>A complete RTC or EOFB was consumed.</summary>
    EndOfBlock,
    /// <summary>Input ended between complete rows with permitted zero padding or declared-Rows zero fill.</summary>
    EndOfInput,
    /// <summary>The configured row limit was reached with EndOfBlock disabled.</summary>
    RowLimit,
    /// <summary>An explicitly enabled incomplete RTC or EOFB was accepted through EOF.</summary>
    IncompleteEndOfBlock
}

/// <summary>Reads most-significant-bit-first CCITT codes from a span using a 64-bit reservoir.</summary>
/// <remarks>
/// <para>Algorithm: store unread bits in the low buffered-bit-count positions, with the next bit
/// at their upper end. Append four bytes in big-endian order when at most 32 bits remain;
/// refill short input tails byte by byte. Peek extracts a prefix without consuming its code.</para>
/// <para>When a complete prefix is already buffered, extract it before loading the next word.
/// This lets prefix decoding and refill proceed independently. Compact lookups may index using
/// zero-padded missing bits, but consumption checks the actual buffered count. Exact reads instead
/// report exhausted input, allowing signed decoding to distinguish truncation from invalid codes.</para>
/// <para>Idea provenance; no article or third-party implementation code was copied:</para>
/// <list type="table">
/// <listheader><term>Source</term><description>Adapted idea</description></listheader>
/// <item><term>Fabian Giesen</term><description>Stateful reservoirs with separate refill, peek
/// and consume operations, and the effect of their dependency chains.
/// See <see href="https://fgiesen.wordpress.com/2018/02/20/reading-bits-in-far-too-many-ways-part-2/">Reading bits in far too many ways, part 2</see>.</description></item>
/// <item><term>Dougall Johnson</term><description>Decode from an already buffered prefix while
/// preparing refill, removing refill from that prefix's dependency chain.
/// See <see href="https://dougallj.wordpress.com/2022/08/26/reading-bits-with-zero-refill-latency/">Reading bits with zero refill latency</see>.</description></item>
/// <item><term>PdfPig (Apache-2.0)</term><description>This bounded MSB-first span reader,
/// 32-bit refill, zero-padded lookup handling and exact-read failure semantics.</description></item>
/// </list>
/// </remarks>
internal ref struct CcittFaxCompactBitReader
{
    // The low bufferedBitCount bits are unread; bit bufferedBitCount - 1 is consumed next.
    // Higher bits are ignored.
    // inputOffset is the index of the next byte to append to bitBuffer.
    private readonly ReadOnlySpan<byte> compressedInput;
    private int inputOffset;
    private int bufferedBitCount;
    private ulong bitBuffer;
    internal CcittFaxCompactBitReader(ReadOnlySpan<byte> compressedInput)
    {
        this.compressedInput = compressedInput;
        inputOffset = 0;
        bufferedBitCount = 0;
        bitBuffer = 0;
    }

    /// <summary>Bytes containing consumed bits, including the final byte's unused bits.</summary>
    /// <remarks>Subtract whole unread bytes so reservoir lookahead is not counted as consumption.
    /// This is ceil((8 * inputOffset - bufferedBitCount) / 8), without multiplying an Int32 offset.</remarks>
    internal int BytesConsumed => inputOffset - bufferedBitCount / 8;

    // T keeps the generic call specialized with its decoder caller; bit extraction itself
    // is identical for every row policy.
    /// <summary>Peeks a code prefix, allowing zero padding only for lookup indexing.</summary>
    /// <remarks>The actual buffered count is unchanged by padding. ConsumeBits must check
    /// the selected code's true length before it is accepted.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int PeekBits<T>(int bitCount)
        where T : struct, ICcittRowPolicy
    {
        // Compute the prefix before loading and appending the next word. The guards ensure
        // that the prefix is complete and the 32-bit append cannot overflow the reservoir.
        if (bufferedBitCount >= bitCount && bufferedBitCount <= 32 && compressedInput.Length - inputOffset >= 4)
        {
            int prefixValue = (int)((bitBuffer >> (bufferedBitCount - bitCount)) & ((1UL << bitCount) - 1));
            ulong refilledBuffer = (bitBuffer << 32) | BinaryPrimitives.ReadUInt32BigEndian(compressedInput.Slice(inputOffset, 4));
            inputOffset += 4;
            bitBuffer = refilledBuffer;
            bufferedBitCount += 32;
            return prefixValue;
        }

        if (bufferedBitCount < bitCount)
            Refill(bitCount);
        return bufferedBitCount >= bitCount
            ? (int)((bitBuffer >> (bufferedBitCount - bitCount)) & ((1UL << bitCount) - 1))
            : (int)((bitBuffer << (bitCount - bufferedBitCount)) & ((1UL << bitCount) - 1));
    }

    private void Refill(int bitCount)
    {
        if (compressedInput.Length - inputOffset >= 4 && bufferedBitCount <= 32)
        {
            bitBuffer = (bitBuffer << 32) | BinaryPrimitives.ReadUInt32BigEndian(compressedInput.Slice(inputOffset, 4));
            bufferedBitCount += 32;
            inputOffset += 4;
        }

        while (bufferedBitCount < bitCount && inputOffset < compressedInput.Length)
        {
            bitBuffer = (bitBuffer << 8) | compressedInput[inputOffset++];
            bufferedBitCount += 8;
        }
    }

    /// <summary>Consumes real buffered bits and rejects an incomplete code.</summary>
    /// <exception cref="InvalidDataException">The requested code length exceeds real buffered bits.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ConsumeBits(int bitCount)
    {
        if (bufferedBitCount < bitCount)
            throw new InvalidDataException("Truncated CCITT code.");
        bufferedBitCount -= bitCount;
    }

    internal int ReadBits<T>(int bitCount)
        where T : struct, ICcittRowPolicy
    {
        var prefixValue = PeekBits<T>(bitCount);
        ConsumeBits(bitCount);
        return prefixValue;
    }

    /// <summary>Checks unread bits up to the next byte boundary before alignment discards them.</summary>
    /// <remarks>This checks only the current partial byte, not the rest of the input. Whole
    /// bytes may still contain an EOL. Because refills append whole bytes, bufferedBitCount
    /// modulo eight equals the number of unread bits to the next input-byte boundary.</remarks>
    internal bool HasZeroAlignmentPadding
    {
        get
        {
            int paddingBits = bufferedBitCount & 7;
            return paddingBits == 0 || PeekBufferedBits(paddingBits) == 0;
        }
    }

    /// <summary>True when the entire remaining input is at most seven zero bits.</summary>
    /// <remarks>Include unread reservoir bits and bytes not loaded into the reservoir. Eight
    /// or more remaining bits cannot be final-byte padding, even if all are zero. This is an
    /// EOF check, unlike HasZeroAlignmentPadding, which permits subsequent whole bytes.</remarks>
    internal bool HasOnlyBytePadding
    {
        get
        {
            long remaining = bufferedBitCount + 8L * (compressedInput.Length - inputOffset);
            if (remaining > 7)
                return false;
            return remaining == 0 || (EnsureBits((int)remaining) && PeekBufferedBits((int)remaining) == 0);
        }
    }

    /// <summary>Discards unread bits of the current partial byte without checking their value.</summary>
    /// <remarks>Callers accepting an EOF or shortened-EOFB exception must check the relevant
    /// zero-padding condition first. Alignment alone is not proof of valid termination.</remarks>
    internal void AlignToByteBoundary() => bufferedBitCount -= bufferedBitCount & 7;
    // An EOL is at least eleven zeros followed by one. Group 3 requires it when EndOfLine
    // is true; otherwise EOL is optional, including for Modified Huffman rows.
    // Mixed Group 3 has a tag bit after the optional or required EOL: one selects 1D, zero 2D.
    // PDF permits optional or required EOL even for Group 4; EOFB is handled separately.
    internal bool ReadRowIsOneDimensional<T>(CcittFaxCompressionType compressionType, bool encodedByteAlign, bool acceptOptionalEndOfLine = false, bool requireEndOfLine = true)
        where T : struct, ICcittRowPolicy
    {
        if (encodedByteAlign)
            AlignToByteBoundary();
        if (compressionType == CcittFaxCompressionType.ModifiedHuffman)
        {
            if (acceptOptionalEndOfLine)
                TryReadEndOfLine();
            return true;
        }
        if (compressionType == CcittFaxCompressionType.Group4_2D)
        {
            if (acceptOptionalEndOfLine)
            {
                bool hasEol = TryReadEndOfLine();
                if (requireEndOfLine && !hasEol)
                    throw new InvalidDataException("Required CCITT EOL is missing.");
            }
            return false;
        }
        if (!requireEndOfLine)
        {
            TryReadEndOfLine();
            return compressionType == CcittFaxCompressionType.Group3_1D || ReadBits<T>(1) != 0;
        }
        if (!TryReadEndOfLine())
            throw new InvalidDataException("Required CCITT EOL is missing.");

        return compressionType == CcittFaxCompressionType.Group3_1D || ReadBits<T>(1) != 0;
    }

    /// <summary>Consumes an EOL, including preceding zero fill, only when the complete marker exists.</summary>
    /// <remarks>Read eleven or more zero bits followed by one. Additional leading zeros are
    /// fill, not additional EOLs: each EOL must end in its own set bit. A copy of the reader
    /// makes a failed probe leave the original logical bit position intact. Row framing and
    /// block-marker recognition both use this primitive; the caller decides its meaning.</remarks>
    internal bool TryReadEndOfLine()
    {
        // Ordinary Huffman/mode prefixes cannot begin with eleven zero bits. Reject them
        // with one reservoir lookup; only possible EOL/fill prefixes need a bitwise probe.
        if (EnsureBits(12) && PeekBufferedBits(12) > 1)
            return false;
        var probe = this;
        int zeroCount = 0;
        while (probe.EnsureBits(1))
        {
            if (probe.ReadBitsExact(1) != 0)
            {
                if (zeroCount < 11)
                    return false;
                this = probe;
                return true;
            }
            if (zeroCount < 11)
                zeroCount++;
        }
        return false;
    }

    /// <summary>Recognizes EOFB/RTC or an explicitly enabled incomplete RTC at EOF without consuming a failed probe.</summary>
    /// <remarks>RTC has six EOLs, each followed by a 1D tag in mixed Group 3. A recovery scan may
    /// already have consumed the first EOL. These marker rules follow PDF Reference 1.7, section 3.3.5.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadEndOfBlock(CcittFaxCompressionType compressionType, bool firstEolAlreadyRead = false,
        bool allowIncompleteRtc = false)
        => ReadEndOfBlockStatus(compressionType, firstEolAlreadyRead, allowIncompleteRtc) != CcittFaxDecodeStatus.NotCompleted;

    /// <summary>Distinguishes complete block markers from explicitly enabled RTC/EOFB exceptions.</summary>
    /// <remarks>
    /// <para>This reader knows coding patterns, not PDF Rows, Height, or repaired-row counts.
    /// The row decoder must verify those prerequisites before enabling either exception.
    /// A full marker is accepted independently of these flags.</para>
    /// <para>Probe a copy so that a failed marker test cannot eat the beginning of the next
    /// image row. Only a successful full or permitted shortened marker commits the copy.
    /// Reservoir lookahead may load bytes, but it does not consume their logical bits.</para>
    /// <para>For Group 3, allowIncompleteRtc permits two to five EOLs followed exclusively
    /// by zero fill through EOF; mixed-format tag bits must all be 1. For Group 4,
    /// allowIncompleteEofb permits one EOL followed exclusively by zero fill through EOF.
    /// allowSingleGroup3Eol additionally permits one Group 3 EOL (with tag 1 for mixed rows),
    /// only after exact positive Rows and with zero fill through EOF. These exceptions
    /// never accept a missing EOL, incorrect tag, or trailing nonzero input.</para>
    /// <para>firstEolAlreadyRead identifies an EOL consumed while recovering a damaged row.
    /// It can contribute to a full standard marker, but cannot establish either shortened
    /// marker exception: doing so would disguise the failed row as a clean image ending.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CcittFaxDecodeStatus ReadEndOfBlockStatus(CcittFaxCompressionType compressionType,
        bool firstEolAlreadyRead = false, bool allowIncompleteRtc = false, bool allowIncompleteEofb = false, bool allowSingleGroup3Eol = false)
    {
        if (!firstEolAlreadyRead && EnsureBits(12) && PeekBufferedBits(12) > 1)
            return CcittFaxDecodeStatus.NotCompleted;
        return ReadEndOfBlockStatusCore(compressionType, firstEolAlreadyRead, allowIncompleteRtc, allowIncompleteEofb, allowSingleGroup3Eol);
    }

    private CcittFaxDecodeStatus ReadEndOfBlockStatusCore(CcittFaxCompressionType compressionType, bool firstEolAlreadyRead,
        bool allowIncompleteRtc, bool allowIncompleteEofb, bool allowSingleGroup3Eol)
    {
        var probe = this;
        int eolCount = compressionType == CcittFaxCompressionType.Group4_2D ? 2 : 6;
        for (int i = 0; i < eolCount; i++)
        {
            if (!(i == 0 && firstEolAlreadyRead) && !probe.TryReadEndOfLine())
            {
                // A single Group 4 EOL is tolerated only when the caller verified the exact
                // declared row count. All remaining bits, including entire bytes, must be zero.
                if (allowIncompleteEofb && compressionType == CcittFaxCompressionType.Group4_2D
                    && !firstEolAlreadyRead && i == 1 && probe.TryReadZeroFillToEnd())
                {
                    probe.AlignToByteBoundary();
                    this = probe;
                    return CcittFaxDecodeStatus.IncompleteEndOfBlock;
                }
                // One Group 3 EOL is a separate local EOF tolerance, not a complete RTC.
                // The caller requires exact positive Rows without repairs. As with shortened RTCs,
                // every remaining bit must be zero; this may include whole zero-fill bytes.
                if (allowSingleGroup3Eol && compressionType != CcittFaxCompressionType.Group4_2D
                    && !firstEolAlreadyRead && i == 1 && probe.TryReadZeroFillToEnd())
                {
                    probe.AlignToByteBoundary();
                    this = probe;
                    return CcittFaxDecodeStatus.IncompleteEndOfBlock;
                }
                // Compatibility at EOF, not a shortened RTC definition: retain complete rows
                // when the caller permits Group 3 termination with at least two EOLs and zero fill.
                // The caller checks the declared row count; this reader only validates the trailer.
                // Poppler similarly retains decoded data after reporting an incomplete RTC:
                // https://skia.googlesource.com/third_party/poppler/+/db9634f3a05c92b3687881259afdf707e1d614d1/poppler/Stream.cc#2252
                if (!allowIncompleteRtc || compressionType == CcittFaxCompressionType.Group4_2D
                    || firstEolAlreadyRead || i < 2 || !probe.TryReadZeroFillToEnd())
                    return CcittFaxDecodeStatus.NotCompleted;
                this = probe;
                return CcittFaxDecodeStatus.IncompleteEndOfBlock;
            }
            if (compressionType == CcittFaxCompressionType.Group3_2D
                && (!probe.EnsureBits(1) || probe.ReadBitsExact(1) != 1))
                return CcittFaxDecodeStatus.NotCompleted;
        }
        this = probe;
        return CcittFaxDecodeStatus.EndOfBlock;
    }

    /// <summary>Consumes the remaining input only when every remaining bit is zero.</summary>
    /// <remarks>This scans buffered bits and input once without allocating an output buffer.
    /// Probe a copy: a nonzero bit preserves the original logical input position. Success
    /// consumes through EOF, including whole zero bytes, so BytesConsumed includes the fill.
    /// Callers must separately authorize this local termination tolerance from exact positive
    /// Rows without repairs, or the existing two-to-five-EOL Group 3 rule. Unknown Rows alone
    /// authorize only HasOnlyBytePadding, not arbitrary zero fill. All-zero tails can also be
    /// truncated code prefixes; the reader cannot distinguish those from intended fill.</remarks>
    internal bool TryReadZeroFillToEnd()
    {
        var probe = this;
        if (!probe.ReadZeroFillToEnd())
            return false;
        this = probe;
        return true;
    }

    private bool ReadZeroFillToEnd()
    {
        while (EnsureBits(1))
        {
            int bitCount = Math.Min(bufferedBitCount, 32);
            if (PeekBufferedBits(bitCount) != 0)
                return false;
            ConsumeBits(bitCount);
        }
        return true;
    }

    /// <summary>Searches forward for the next complete EOL after a damaged Group 3 row.</summary>
    internal bool TryResynchronizeAtEndOfLine()
    {
        int zeroCount = 0;
        while (EnsureBits(1))
        {
            if (ReadBitsExact(1) == 0)
            {
                if (zeroCount < 11)
                    zeroCount++;
            }
            else
            {
                if (zeroCount >= 11)
                    return true;
                zeroCount = 0;
            }
        }
        return false;
    }

    internal int ReadModeBitByBit<T>()
        where T : struct, ICcittRowPolicy
    {
        // Vertical codes give offsets from a reference-row transition: 1 means zero,
        // 011/010 mean +1/-1. Horizontal (001) reads two runs; pass (0001) skips a reference pair.
        if (ReadBits<T>(1) != 0)
            return 0;
        var middleBits = ReadBits<T>(2);
        if (middleBits == 1)
            return 100;
        if (middleBits >= 2)
            return middleBits == 3 ? 1 : -1;
        if (ReadBits<T>(1) != 0)
            return 101;
        var tailBits = ReadBits<T>(2);
        if (tailBits >= 2)
            return tailBits == 3 ? 2 : -2;
        if (tailBits == 1)
            return ReadBits<T>(1) != 0 ? 3 : -3;
        throw new InvalidDataException("Unsupported extension, EOFB or invalid CCITT mode.");
    }

    /// <summary>Refills until the requested prefix is present or compressed input ends.</summary>
    /// <returns>True only when all requested bits come from real input.</returns>
    internal bool EnsureBits(int bitCount)
    {
        if (bufferedBitCount < bitCount)
            Refill(bitCount);
        return bufferedBitCount >= bitCount;
    }

    // Call only after EnsureBits(bitCount) returned true; the prefix is then fully buffered.
    internal int PeekBufferedBits(int bitCount) => (int)((bitBuffer >> (bufferedBitCount - bitCount)) & ((1UL << bitCount) - 1));
    /// <summary>Reads and consumes a complete code fragment without zero padding.</summary>
    /// <exception cref="EndOfStreamException">Compressed input ends before the requested bits are available.</exception>
    internal int ReadBitsExact(int bitCount)
    {
        if (!EnsureBits(bitCount))
            throw new EndOfStreamException("Unexpected end of Huffman RLE stream");
        int prefixValue = PeekBufferedBits(bitCount);
        bufferedBitCount -= bitCount;
        return prefixValue;
    }
}
