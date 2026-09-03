namespace UglyToad.PdfPig.Filters.Flate
{
    using System;
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Inflates a raw deflate stream (RFC 1951) that lies complete in memory into a buffer that
    /// grows as needed and doubles as the sliding window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built for how PDF uses deflate: every stream is in memory in one piece, so there is no
    /// streaming state to keep between calls, no separate window to maintain and no copy from a
    /// window into the output. Bits are read from a 64-bit buffer refilled eight bytes at a time,
    /// codes are looked up in two-level tables, and matches are copies within the output.
    /// </para>
    /// <para>
    /// Damaged or cut short input is not an error: everything decoded up to that point is kept
    /// and the outcome says what happened, which is what a filter reading damaged documents wants
    /// and what the stream classes cannot give without losing the last block.
    /// </para>
    /// </remarks>
    internal static class Inflater
    {
        /// <summary>How an inflate ended.</summary>
        public enum Outcome
        {
            /// <summary>The final block ended properly.</summary>
            Complete,

            /// <summary>The input ran out before the final block ended; the output holds what there was.</summary>
            Truncated,

            /// <summary>The input is not a valid deflate stream from some point on; the output holds what decoded before it.</summary>
            Damaged
        }

        /// <summary>The longest match a length code can stand for.</summary>
        private const int MaxMatchLength = 258;

        /// <summary>
        /// Kept free past the written output so that one pass of the fast loop, three literals or a
        /// match with a copy that spills a word past its end, needs no check per byte.
        /// </summary>
        private const int OutputSlack = MaxMatchLength + 32;

        /// <summary>
        /// Input left from which the fast loop can refill twice by whole words: a refill takes at
        /// most seven bytes, so sixteen leave a word for the second one.
        /// </summary>
        private const int FastLoopInputBytes = 16;

        private const int LiteralLengthMainBits = 10;
        private const int DistanceMainBits = 8;
        private const int PrecodeMainBits = 7;

        private const int EndOfBlock = 256;
        private const int FirstLengthSymbol = 257;
        private const int LiteralLengthSymbols = 288;
        private const int DistanceSymbols = 32;
        private const int PrecodeSymbols = 19;
        private const int MaxCodeLength = 15;

        // A table entry packs everything the decoder needs, so that a symbol costs one lookup and
        // one shift: bits 0-7 hold the number of bits the codeword and its extra bits take together,
        // bits 8-11 the codeword's length alone, to shift the codeword off the extra bits, bits
        // 12-15 what kind of entry it is, and bits 16-31 the literal, the base length or distance,
        // the precode symbol, or the offset of a subtable. An entry of zero is no code. The layout
        // follows libdeflate.
        private const uint LiteralFlag = 0x1000;
        private const uint EndOfBlockFlag = 0x2000;
        private const uint SubtableFlag = 0x4000;
        private const int CodeLengthShift = 8;
        private const int ValueShift = 16;

        private enum TableKind
        {
            Precode,
            LiteralLength,
            Distance
        }

        /// <summary>
        /// The most entries the subtables of one table can take: zlib's enough program gives 308 for
        /// 288 symbols behind a 10-bit main table and 146 for 32 symbols behind an 8-bit one.
        /// </summary>
        private const int MaxSubtableEntries = 512;

        private const int MaximumCapacity = 0x7FFFFFC7;

        private static readonly ushort[] LengthBase =
        [
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
        ];

        private static readonly byte[] LengthExtraBits =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
        ];

        private static readonly ushort[] DistanceBase =
        [
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577
        ];

        private static readonly byte[] DistanceExtraBits =
        [
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13
        ];

        /// <summary>The order in which the code length code lengths are stored (RFC 1951, 3.2.7).</summary>
        private static readonly byte[] PrecodeOrder =
        [
            16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15
        ];

        private static readonly int FixedLiteralLengthBits;
        private static readonly int FixedDistanceBits;
        private static readonly uint[] FixedLiteralLengthTable = BuildFixedLiteralLengthTable(out FixedLiteralLengthBits);
        private static readonly uint[] FixedDistanceTable = BuildFixedDistanceTable(out FixedDistanceBits);

        /// <summary>
        /// Inflates <paramref name="input"/> into <paramref name="output"/>, which is taken from and
        /// grown through <see cref="ArrayPool{T}.Shared"/>, and returns the number of bytes written.
        /// </summary>
        /// <param name="input">The raw deflate data, without any zlib header.</param>
        /// <param name="output">A rented buffer to write to; replaced by a larger rented buffer when it fills.</param>
        /// <param name="outcome">Whether the stream ended properly, ran out, or turned out damaged.</param>
        public static int Inflate(ReadOnlySpan<byte> input, ref byte[] output, out Outcome outcome)
        {
            var reader = new BitReader(input);
            var written = 0;

            uint[]? literalLengthTable = null;
            uint[]? distanceTable = null;
            uint[]? precodeTable = null;

            try
            {
                while (true)
                {
                    reader.Refill();

                    if (reader.Count < 3)
                    {
                        outcome = Outcome.Truncated;
                        return written;
                    }

                    var isFinal = reader.Peek(1) != 0;
                    reader.Consume(1);
                    var blockType = (int)reader.Peek(2);
                    reader.Consume(2);

                    Outcome blockOutcome;

                    switch (blockType)
                    {
                        case 0:
                            blockOutcome = CopyStoredBlock(ref reader, ref output, ref written);
                            break;
                        case 1:
                            blockOutcome = DecodeBlock(ref reader, ref output, ref written, FixedLiteralLengthTable, FixedLiteralLengthBits, FixedDistanceTable, FixedDistanceBits);
                            break;
                        case 2:
                            literalLengthTable ??= ArrayPool<uint>.Shared.Rent((1 << LiteralLengthMainBits) + MaxSubtableEntries);
                            distanceTable ??= ArrayPool<uint>.Shared.Rent((1 << DistanceMainBits) + MaxSubtableEntries);
                            precodeTable ??= ArrayPool<uint>.Shared.Rent(1 << PrecodeMainBits);

                            blockOutcome = ReadDynamicTables(ref reader, ref literalLengthTable, out var literalLengthBits, ref distanceTable, out var distanceBits, ref precodeTable);

                            if (blockOutcome == Outcome.Complete)
                            {
                                blockOutcome = DecodeBlock(ref reader, ref output, ref written, literalLengthTable, literalLengthBits, distanceTable, distanceBits);
                            }

                            break;
                        default:
                            blockOutcome = Outcome.Damaged;
                            break;
                    }

                    if (blockOutcome != Outcome.Complete)
                    {
                        outcome = blockOutcome;
                        return written;
                    }

                    if (isFinal)
                    {
                        outcome = Outcome.Complete;
                        return written;
                    }
                }
            }
            finally
            {
                if (literalLengthTable != null)
                {
                    ArrayPool<uint>.Shared.Return(literalLengthTable);
                }

                if (distanceTable != null)
                {
                    ArrayPool<uint>.Shared.Return(distanceTable);
                }

                if (precodeTable != null)
                {
                    ArrayPool<uint>.Shared.Return(precodeTable);
                }
            }
        }

        /// <summary>A stored block: a length, its complement, and that many bytes as they are.</summary>
        private static Outcome CopyStoredBlock(ref BitReader reader, ref byte[] output, ref int written)
        {
            // The bit buffer is emptied back into the input so that the header and the bytes are
            // read from the input directly and no bit of a byte lingers in the buffer.
            reader.AlignToByteAndFlush();

            if (reader.Remaining < 4)
            {
                return Outcome.Truncated;
            }

            var length = reader.ReadUInt16();
            var complement = reader.ReadUInt16();

            if (length != (~complement & 0xFFFF))
            {
                return Outcome.Damaged;
            }

            EnsureCapacity(ref output, written, length);

            var copied = reader.CopyInput(output.AsSpan(written, length));
            written += copied;

            return copied < length ? Outcome.Truncated : Outcome.Complete;
        }

        /// <summary>Reads the code length codes, then the literal/length and distance code lengths, and builds the tables.</summary>
        private static Outcome ReadDynamicTables(ref BitReader reader, ref uint[] literalLengthTable, out int literalLengthBits, ref uint[] distanceTable, out int distanceBits, ref uint[] precodeTable)
        {
            literalLengthBits = LiteralLengthMainBits;
            distanceBits = DistanceMainBits;

            reader.Refill();

            if (reader.Count < 14)
            {
                return Outcome.Truncated;
            }

            var literalLengthCount = (int)reader.Peek(5) + 257;
            reader.Consume(5);
            var distanceCount = (int)reader.Peek(5) + 1;
            reader.Consume(5);
            var precodeCount = (int)reader.Peek(4) + 4;
            reader.Consume(4);

            if (literalLengthCount > 286 || distanceCount > 30)
            {
                return Outcome.Damaged;
            }

            Span<byte> precodeLengths = stackalloc byte[PrecodeSymbols];
            precodeLengths.Clear();

            for (var i = 0; i < precodeCount; i++)
            {
                reader.Refill();

                if (reader.Count < 3)
                {
                    return Outcome.Truncated;
                }

                precodeLengths[PrecodeOrder[i]] = (byte)reader.Peek(3);
                reader.Consume(3);
            }

            var precodeBits = PrecodeMainBits;

            if (!BuildTable(TableKind.Precode, precodeLengths, ref precodeBits, ref precodeTable, allowSingleCode: false))
            {
                return Outcome.Damaged;
            }

            Span<byte> codeLengths = stackalloc byte[LiteralLengthSymbols + DistanceSymbols];
            codeLengths.Clear();

            var total = literalLengthCount + distanceCount;
            var read = 0;

            while (read < total)
            {
                reader.Refill();

                var symbol = DecodeSymbol(ref reader, precodeTable, precodeBits);

                if (symbol < 0)
                {
                    return symbol == Truncated ? Outcome.Truncated : Outcome.Damaged;
                }

                if (symbol < 16)
                {
                    codeLengths[read++] = (byte)symbol;
                    continue;
                }

                int repeat;
                byte value;

                if (symbol == 16)
                {
                    if (read == 0)
                    {
                        return Outcome.Damaged;
                    }

                    if (reader.Count < 2)
                    {
                        return Outcome.Truncated;
                    }

                    value = codeLengths[read - 1];
                    repeat = 3 + (int)reader.Peek(2);
                    reader.Consume(2);
                }
                else if (symbol == 17)
                {
                    if (reader.Count < 3)
                    {
                        return Outcome.Truncated;
                    }

                    value = 0;
                    repeat = 3 + (int)reader.Peek(3);
                    reader.Consume(3);
                }
                else
                {
                    if (reader.Count < 7)
                    {
                        return Outcome.Truncated;
                    }

                    value = 0;
                    repeat = 11 + (int)reader.Peek(7);
                    reader.Consume(7);
                }

                if (read + repeat > total)
                {
                    return Outcome.Damaged;
                }

                codeLengths.Slice(read, repeat).Fill(value);
                read += repeat;
            }

            // The end of block code has to exist, or the block could never end.
            if (codeLengths[EndOfBlock] == 0)
            {
                return Outcome.Damaged;
            }

            if (!BuildTable(TableKind.LiteralLength, codeLengths.Slice(0, literalLengthCount), ref literalLengthBits, ref literalLengthTable)
                || !BuildTable(TableKind.Distance, codeLengths.Slice(literalLengthCount, distanceCount), ref distanceBits, ref distanceTable))
            {
                return Outcome.Damaged;
            }

            return Outcome.Complete;
        }

        /// <summary>Decodes the symbols of one block until its end of block code.</summary>
        private static Outcome DecodeBlock(ref BitReader reader, ref byte[] output, ref int written, uint[] literalLengthTable, int literalLengthBits, uint[] distanceTable, int distanceBits)
        {
            var literalLengthMask = (1u << literalLengthBits) - 1;
            var distanceMask = (1u << distanceBits) - 1;

            while (true)
            {
                // The fast loop: while the input has whole words to load and the output has slack
                // for the most one pass can write, nothing needs a check per symbol. A refill
                // leaves at least 56 bits, enough for three literals of up to 15 bits, or for two
                // literals and a length with its extra bits; a second refill precedes the distance.
                // The literals decoded straight after one another follow libdeflate.
                var preloaded = 0u;
                var hasPreloaded = false;

                while (reader.Remaining >= FastLoopInputBytes && output.Length - written >= OutputSlack)
                {
                    reader.Refill();

                    var fastEntry = hasPreloaded ? preloaded : literalLengthTable[(int)(reader.Bits & literalLengthMask)];
                    hasPreloaded = false;

                    if ((fastEntry & LiteralFlag) != 0)
                    {
                        reader.Consume((int)(fastEntry & 0xFF));
                        output[written++] = (byte)(fastEntry >> ValueShift);

                        fastEntry = literalLengthTable[(int)(reader.Bits & literalLengthMask)];

                        if ((fastEntry & LiteralFlag) != 0)
                        {
                            reader.Consume((int)(fastEntry & 0xFF));
                            output[written++] = (byte)(fastEntry >> ValueShift);

                            fastEntry = literalLengthTable[(int)(reader.Bits & literalLengthMask)];

                            if ((fastEntry & LiteralFlag) != 0)
                            {
                                reader.Consume((int)(fastEntry & 0xFF));
                                output[written++] = (byte)(fastEntry >> ValueShift);
                                continue;
                            }
                        }
                    }

                    if ((fastEntry & SubtableFlag) != 0)
                    {
                        reader.Consume(literalLengthBits);
                        fastEntry = literalLengthTable[(int)(fastEntry >> ValueShift) + (int)(reader.Bits & ((1u << ((int)(fastEntry >> CodeLengthShift) & 0xF)) - 1))];

                        if ((fastEntry & LiteralFlag) != 0)
                        {
                            reader.Consume((int)(fastEntry & 0xFF));
                            output[written++] = (byte)(fastEntry >> ValueShift);
                            continue;
                        }
                    }

                    var fastTotal = (int)(fastEntry & 0xFF);

                    if (fastTotal == 0)
                    {
                        return Outcome.Damaged;
                    }

                    if ((fastEntry & EndOfBlockFlag) != 0)
                    {
                        reader.Consume(fastTotal);
                        return Outcome.Complete;
                    }

                    var fastSaved = reader.Bits;
                    reader.Consume(fastTotal);

                    var fastLength = (int)(fastEntry >> ValueShift) + (int)((fastSaved & ((1UL << fastTotal) - 1)) >> ((int)(fastEntry >> CodeLengthShift) & 0xF));

                    reader.Refill();

                    fastEntry = distanceTable[(int)(reader.Bits & distanceMask)];

                    if ((fastEntry & SubtableFlag) != 0)
                    {
                        reader.Consume(distanceBits);
                        fastEntry = distanceTable[(int)(fastEntry >> ValueShift) + (int)(reader.Bits & ((1u << ((int)(fastEntry >> CodeLengthShift) & 0xF)) - 1))];
                    }

                    fastTotal = (int)(fastEntry & 0xFF);

                    if (fastTotal == 0)
                    {
                        return Outcome.Damaged;
                    }

                    fastSaved = reader.Bits;
                    reader.Consume(fastTotal);

                    var fastDistance = (int)(fastEntry >> ValueShift) + (int)((fastSaved & ((1UL << fastTotal) - 1)) >> ((int)(fastEntry >> CodeLengthShift) & 0xF));

                    if (fastDistance > written)
                    {
                        return Outcome.Damaged;
                    }

                    // The entry after the match is looked up before the copy so that its load
                    // overlaps the copy; at least 28 bits are still buffered, enough for any code.
                    preloaded = literalLengthTable[(int)(reader.Bits & literalLengthMask)];
                    hasPreloaded = true;

                    CopyMatch(output, written, fastDistance, fastLength);
                    written += fastLength;
                }

                // Near the end of the input, or with the output nearly full: one symbol with every
                // check, after which the fast loop gets another chance.
                reader.Refill();

                if (output.Length - written < OutputSlack)
                {
                    EnsureCapacity(ref output, written, OutputSlack);
                }

                var entry = literalLengthTable[(int)(reader.Bits & literalLengthMask)];

                if ((entry & SubtableFlag) != 0)
                {
                    if (reader.Count < literalLengthBits)
                    {
                        return Outcome.Truncated;
                    }

                    reader.Consume(literalLengthBits);
                    entry = literalLengthTable[(int)(entry >> ValueShift) + (int)(reader.Bits & ((1u << ((int)(entry >> CodeLengthShift) & 0xF)) - 1))];
                }

                var total = (int)(entry & 0xFF);

                if (total == 0)
                {
                    return Outcome.Damaged;
                }

                if (total > reader.Count)
                {
                    return Outcome.Truncated;
                }

                var saved = reader.Bits;
                reader.Consume(total);

                if ((entry & LiteralFlag) != 0)
                {
                    output[written++] = (byte)(entry >> ValueShift);
                    continue;
                }

                if ((entry & EndOfBlockFlag) != 0)
                {
                    return Outcome.Complete;
                }

                // The extra bits sit above the codeword in what was consumed.
                var length = (int)(entry >> ValueShift) + (int)((saved & ((1UL << total) - 1)) >> ((int)(entry >> CodeLengthShift) & 0xF));

                entry = distanceTable[(int)(reader.Bits & distanceMask)];

                if ((entry & SubtableFlag) != 0)
                {
                    if (reader.Count < distanceBits)
                    {
                        return Outcome.Truncated;
                    }

                    reader.Consume(distanceBits);
                    entry = distanceTable[(int)(entry >> ValueShift) + (int)(reader.Bits & ((1u << ((int)(entry >> CodeLengthShift) & 0xF)) - 1))];
                }

                total = (int)(entry & 0xFF);

                if (total == 0)
                {
                    return Outcome.Damaged;
                }

                if (total > reader.Count)
                {
                    return Outcome.Truncated;
                }

                saved = reader.Bits;
                reader.Consume(total);

                var distance = (int)(entry >> ValueShift) + (int)((saved & ((1UL << total) - 1)) >> ((int)(entry >> CodeLengthShift) & 0xF));

                if (distance > written)
                {
                    // Reaches back before the start of the output.
                    return Outcome.Damaged;
                }

                CopyMatch(output, written, distance, length);
                written += length;
            }
        }

        /// <summary>
        /// Copies <paramref name="length"/> bytes from <paramref name="distance"/> bytes back. Source and
        /// destination overlap when the distance is shorter than the length, and then the copy has to
        /// proceed in the order that repeats the pattern.
        /// </summary>
        private static void CopyMatch(byte[] output, int written, int distance, int length)
        {
            var source = written - distance;

            if (distance >= sizeof(ulong))
            {
                // Most matches are a few bytes long, and a call into the block copy costs more
                // than moving a word or two. Eight bytes at a time never read what this copy has
                // written when the distance is at least eight, and the buffer has slack past the
                // match for the last word to spill into.
                // Two words unconditionally, which covers most matches, then a word per step for
                // the rest; a call into the block copy would cost more than the words it saves.
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(written), BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(source)));
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(written + sizeof(ulong)), BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(source + sizeof(ulong))));

                if (length <= 2 * sizeof(ulong))
                {
                    return;
                }

                var end = written + length;
                written += 2 * sizeof(ulong);
                source += 2 * sizeof(ulong);

                do
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(written), BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(source)));
                    written += sizeof(ulong);
                    source += sizeof(ulong);
                } while (written < end);

                return;
            }

            if (distance == 1)
            {
                output.AsSpan(written, length).Fill(output[source]);
                return;
            }

            // A pattern shorter than a word repeats itself: the first copies grow the pattern to
            // a word, after which whole words carry it on.
            for (var i = 0; i < length; i++)
            {
                output[written + i] = output[source + i];
            }
        }

        private const int Truncated = -1;
        private const int Invalid = -2;

        /// <summary>
        /// Decodes one symbol with a two-level table, or returns <see cref="Truncated"/> when the input
        /// has run out or <see cref="Invalid"/> when the bits are not a code of the table.
        /// </summary>
        private static int DecodeSymbol(ref BitReader reader, uint[] table, int mainBits)
        {
            var entry = table[(int)(reader.Bits & ((1u << mainBits) - 1))];
            var consumed = 0;

            if ((entry & SubtableFlag) != 0)
            {
                var subtableBits = (int)(entry >> CodeLengthShift) & 0xF;
                var offset = (int)(entry >> ValueShift);

                entry = table[offset + (int)((reader.Bits >> mainBits) & ((1u << subtableBits) - 1))];
                consumed = mainBits;
            }

            var total = (int)(entry & 0xFF);

            if (total == 0)
            {
                return Invalid;
            }

            consumed += total;

            if (consumed > reader.Count)
            {
                return Truncated;
            }

            reader.Consume(consumed);

            return (int)(entry >> ValueShift);
        }

        /// <summary>The table entry for <paramref name="symbol"/> with a code of <paramref name="codeLength"/> bits; see the entry layout at the flags.</summary>
        private static uint MakeEntry(TableKind kind, int symbol, int codeLength)
        {
            var lengthBits = (uint)codeLength << CodeLengthShift;

            switch (kind)
            {
                case TableKind.LiteralLength:
                    if (symbol < EndOfBlock)
                    {
                        return LiteralFlag | ((uint)symbol << ValueShift) | lengthBits | (uint)codeLength;
                    }

                    if (symbol == EndOfBlock)
                    {
                        return EndOfBlockFlag | lengthBits | (uint)codeLength;
                    }

                    var lengthIndex = symbol - FirstLengthSymbol;

                    if (lengthIndex >= LengthBase.Length)
                    {
                        // 286 and 287 can be given codes but stand for nothing.
                        return 0;
                    }

                    return ((uint)LengthBase[lengthIndex] << ValueShift) | lengthBits | (uint)(codeLength + LengthExtraBits[lengthIndex]);
                case TableKind.Distance:
                    if (symbol >= DistanceBase.Length)
                    {
                        // 30 and 31 likewise.
                        return 0;
                    }

                    return ((uint)DistanceBase[symbol] << ValueShift) | lengthBits | (uint)(codeLength + DistanceExtraBits[symbol]);
                default:
                    return ((uint)symbol << ValueShift) | lengthBits | (uint)codeLength;
            }
        }

        /// <summary>
        /// Builds a two-level decoding table from code lengths: a main table indexed by the low
        /// <paramref name="mainBits"/> of the input, whose entries either name a symbol and its code
        /// length or point at a subtable for the codes longer than that. Returns false when the code
        /// lengths do not form a usable code.
        /// </summary>
        private static bool BuildTable(TableKind kind, ReadOnlySpan<byte> codeLengths, ref int mainBits, ref uint[] table, bool allowSingleCode = true)
        {
            Span<int> countByLength = stackalloc int[MaxCodeLength + 1];
            countByLength.Clear();

            foreach (var length in codeLengths)
            {
                if (length > MaxCodeLength)
                {
                    return false;
                }

                countByLength[length]++;
            }

            countByLength[0] = 0;

            var anyCode = false;
            for (var length = 1; length <= MaxCodeLength; length++)
            {
                if (countByLength[length] > 0)
                {
                    anyCode = true;
                    break;
                }
            }

            if (!anyCode)
            {
                // No codes at all, which a distance code may have when a block contains no
                // matches: any distance code read is then invalid.
                mainBits = 1;
                table[0] = 0;
                table[1] = 0;
                return true;
            }

            // Over-subscribed codes cannot be decoded. Incomplete ones are refused as zlib refuses
            // them, so that damage is noticed at the same place: the one exception, also zlib's,
            // is a code consisting of a single one-bit code, which a stream with a single distance
            // legitimately has.
            var left = 1;
            var longest = 0;
            for (var length = 1; length <= MaxCodeLength; length++)
            {
                left = (left << 1) - countByLength[length];

                if (left < 0)
                {
                    return false;
                }

                if (countByLength[length] > 0)
                {
                    longest = length;
                }
            }

            if (left > 0 && (longest != 1 || !allowSingleCode))
            {
                return false;
            }

            // Symbols sorted by code length, and within a length by symbol, which is the order of
            // canonical codewords: a counting sort over the lengths.
            Span<int> firstOfLength = stackalloc int[MaxCodeLength + 2];
            firstOfLength[0] = 0;
            firstOfLength[1] = 0;
            for (var length = 1; length <= MaxCodeLength; length++)
            {
                firstOfLength[length + 1] = firstOfLength[length] + countByLength[length];
            }

            Span<short> sortedSymbols = stackalloc short[codeLengths.Length];
            for (var symbol = 0; symbol < codeLengths.Length; symbol++)
            {
                var length = codeLengths[symbol];

                if (length != 0)
                {
                    sortedSymbols[firstOfLength[length]++] = (short)symbol;
                }
            }

            if (left > 0)
            {
                // The single one-bit code: its codeword is 0, and 1 is not a code.
                mainBits = 1;
                table[0] = MakeEntry(kind, sortedSymbols[0], 1);
                table[1] = 0;
                return true;
            }

            // No wider than the longest code: a code no longer than the main table is looked up in
            // one step, and a code that is all short gets a small table, which matters for the many
            // streams that are a few kilobytes long and build a table for each.
            mainBits = Math.Min(mainBits, longest);

            var mainSize = 1 << mainBits;

            // The subtables cannot be sized before the codes are walked, so the table has room for
            // the worst case up front.
            if (table.Length < mainSize + MaxSubtableEntries)
            {
                ArrayPool<uint>.Shared.Return(table);
                table = ArrayPool<uint>.Shared.Rent(mainSize + MaxSubtableEntries);
            }

            // Codewords in the order they are read, least significant bit first. A codeword is
            // written once, at its own index; when the length grows by one the filled part of the
            // table is copied behind itself, which is how each codeword comes to stand at every
            // index whose low bits are it. Copying whole spans is far cheaper than the strided
            // stores that would otherwise visit each index on its own. The technique is libdeflate's.
            var next = 0;
            var codeword = 0;
            var codeLength = 1;
            var count = countByLength[1];

            while (count == 0)
            {
                count = countByLength[++codeLength];
            }

            var filled = 1 << codeLength;

            while (codeLength <= mainBits)
            {
                do
                {
                    table[codeword] = MakeEntry(kind, sortedSymbols[next++], codeLength);

                    if (codeword == filled - 1)
                    {
                        // The last codeword of a code that fits the main table entirely.
                        for (; codeLength < mainBits; codeLength++)
                        {
                            table.AsSpan(0, filled).CopyTo(table.AsSpan(filled, filled));
                            filled <<= 1;
                        }

                        return true;
                    }

                    codeword = NextCodeword(codeword, filled - 1);
                } while (--count != 0);

                do
                {
                    if (++codeLength <= mainBits)
                    {
                        table.AsSpan(0, filled).CopyTo(table.AsSpan(filled, filled));
                        filled <<= 1;
                    }

                    count = countByLength[codeLength];
                } while (count == 0);
            }

            // The codewords longer than the main table: each distinct low part points at a
            // subtable, sized so that the codewords sharing that low part fill it completely.
            var mainMask = mainSize - 1;
            var subtablePrefix = -1;
            var subtableStart = mainSize;
            var subtableEnd = mainSize;

            while (true)
            {
                if ((codeword & mainMask) != subtablePrefix)
                {
                    subtablePrefix = codeword & mainMask;
                    subtableStart = subtableEnd;

                    var subtableBits = codeLength - mainBits;
                    var codespaceUsed = count;

                    while (codespaceUsed < 1 << subtableBits)
                    {
                        subtableBits++;
                        codespaceUsed = (codespaceUsed << 1) + countByLength[mainBits + subtableBits];
                    }

                    subtableEnd = subtableStart + (1 << subtableBits);
                    table[subtablePrefix] = SubtableFlag | ((uint)subtableStart << ValueShift) | ((uint)subtableBits << CodeLengthShift);
                }

                var remainingLength = codeLength - mainBits;
                var entry = MakeEntry(kind, sortedSymbols[next++], remainingLength);
                var stride = 1 << remainingLength;

                for (var i = subtableStart + (codeword >> mainBits); i < subtableEnd; i += stride)
                {
                    table[i] = entry;
                }

                if (codeword == (1 << codeLength) - 1)
                {
                    return true;
                }

                codeword = NextCodeword(codeword, (1 << codeLength) - 1);

                if (--count == 0)
                {
                    do
                    {
                        count = countByLength[++codeLength];
                    } while (count == 0);
                }
            }
        }

        /// <summary>
        /// The codeword after <paramref name="codeword"/> in canonical order, both stored least
        /// significant bit first: the highest zero bit is set and the ones above it cleared.
        /// </summary>
        private static int NextCodeword(int codeword, int allOnes)
        {
            var bit = 1 << HighestBit(codeword ^ allOnes);
            return (codeword & (bit - 1)) | bit;
        }

        private static int HighestBit(int value)
        {
#if NETCOREAPP3_0_OR_GREATER
            return System.Numerics.BitOperations.Log2((uint)value);
#else
            var bit = 0;
            while ((value >>= 1) != 0)
            {
                bit++;
            }

            return bit;
#endif
        }

        private static uint[] BuildFixedLiteralLengthTable(out int mainBits)
        {
            Span<byte> lengths = stackalloc byte[LiteralLengthSymbols];
            lengths.Slice(0, 144).Fill(8);
            lengths.Slice(144, 112).Fill(9);
            lengths.Slice(256, 24).Fill(7);
            lengths.Slice(280, 8).Fill(8);

            var table = new uint[(1 << LiteralLengthMainBits) + MaxSubtableEntries];
            mainBits = LiteralLengthMainBits;
            BuildTable(TableKind.LiteralLength, lengths, ref mainBits, ref table);

            return table;
        }

        private static uint[] BuildFixedDistanceTable(out int mainBits)
        {
            Span<byte> lengths = stackalloc byte[DistanceSymbols];
            lengths.Fill(5);

            var table = new uint[(1 << DistanceMainBits) + MaxSubtableEntries];
            mainBits = DistanceMainBits;
            BuildTable(TableKind.Distance, lengths, ref mainBits, ref table);

            return table;
        }

        /// <summary>Makes room for <paramref name="required"/> more bytes after <paramref name="written"/>.</summary>
        private static void EnsureCapacity(ref byte[] output, int written, int required)
        {
            if (output.Length - written >= required)
            {
                return;
            }

            var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(MaximumCapacity, Math.Max((long)written + required, output.Length * 2L)));

            output.AsSpan(0, written).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(output);

            output = grown;
        }

        /// <summary>
        /// Serves the input as bits, least significant first, from a 64-bit buffer refilled eight
        /// bytes at a time.
        /// </summary>
        private ref struct BitReader
        {
            private readonly ReadOnlySpan<byte> input;
            private int position;

            public BitReader(ReadOnlySpan<byte> input)
            {
                this.input = input;
                position = 0;
                Bits = 0;
                Count = 0;
            }

            /// <summary>The buffered bits, the next one to read in the lowest position.</summary>
            public ulong Bits { get; private set; }

            /// <summary>How many of <see cref="Bits"/> are valid.</summary>
            public int Count { get; private set; }

            /// <summary>
            /// Tops the buffer up to at least 56 bits while input remains. A whole word is loaded
            /// and only the bytes that fit are consumed, so the load needs no length calculation.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Refill()
            {
                if (position + sizeof(ulong) <= input.Length)
                {
                    Bits |= BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(position)) << Count;
                    position += (63 - Count) >> 3;
                    Count |= 56;
                    return;
                }

                while (Count <= 56 && position < input.Length)
                {
                    Bits |= (ulong)input[position++] << Count;
                    Count += 8;
                }
            }

            public uint Peek(int count) => (uint)(Bits & ((1UL << count) - 1));

            public void Consume(int count)
            {
                Bits >>= count;
                Count -= count;
            }

            /// <summary>How many input bytes are neither in the buffer nor read yet.</summary>
            public int Remaining => input.Length - position;

            /// <summary>
            /// Drops the bits up to the next byte boundary and hands the whole bytes in the buffer
            /// back to the input, so that a stored block can be read from the input directly.
            /// </summary>
            public void AlignToByteAndFlush()
            {
                Count -= Count & 7;
                position -= Count >> 3;
                Bits = 0;
                Count = 0;
            }

            /// <summary>Reads two bytes straight from the input; the buffer must be empty.</summary>
            public int ReadUInt16()
            {
                var value = input[position] | (input[position + 1] << 8);
                position += 2;
                return value;
            }

            /// <summary>Copies bytes straight from the input, which the bit buffer must be empty for, and returns how many there were.</summary>
            public int CopyInput(Span<byte> destination)
            {
                var available = Math.Min(destination.Length, input.Length - position);

                input.Slice(position, available).CopyTo(destination);
                position += available;

                return available;
            }
        }
    }
}
