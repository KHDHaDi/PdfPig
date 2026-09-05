namespace UglyToad.PdfPig.Tokenization
{
    using System.Collections.Generic;
    using Core;
    using Scanner;
    using Tokens;

    internal sealed class ArrayTokenizer : ITokenizer
    {
        private readonly bool usePdfDocEncoding;
        private readonly StackDepthGuard stackDepthGuard;
        private readonly bool useLenientParsing;

        // The tokens of an array are copied into the ArrayToken, so the list they are gathered in
        // is kept and reused instead of being grown from empty for every array. A content stream
        // with kerned text has one array per TJ operator.
        private List<IToken> gathered;

        public bool ReadsNextByte { get; } = false;

        public ArrayTokenizer(bool usePdfDocEncoding, StackDepthGuard stackDepthGuard, bool useLenientParsing)
        {
            this.usePdfDocEncoding = usePdfDocEncoding;
            this.stackDepthGuard = stackDepthGuard;
            this.useLenientParsing = useLenientParsing;
        }

        public bool TryTokenize(byte currentByte, IInputBytes inputBytes, out IToken token)
        {
            token = null;

            if (currentByte != '[')
            {
                return false;
            }

            var scanner = new CoreTokenScanner(inputBytes, usePdfDocEncoding, stackDepthGuard, ScannerScope.Array, useLenientParsing: useLenientParsing);

            var contents = gathered ??= new List<IToken>();
            contents.Clear();

            IToken previousToken = null;
            while (!CurrentByteEndsCurrentArray(inputBytes, previousToken) && scanner.MoveNext())
            {
                previousToken = scanner.CurrentToken;

                if (scanner.CurrentToken is CommentToken)
                {
                    continue;
                }
                
                contents.Add(scanner.CurrentToken);
            }

            token = new ArrayToken(contents);
            contents.Clear();

            return true;
        }

        private static bool CurrentByteEndsCurrentArray(IInputBytes inputBytes, IToken previousToken)
        {
            if (inputBytes.CurrentByte == ']' && !(previousToken is ArrayToken))
            {
                return true;
            }

            return false;
        }
    }
}
