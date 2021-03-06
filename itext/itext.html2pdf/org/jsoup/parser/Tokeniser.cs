using System;
using System.Text;
using Org.Jsoup;
using Org.Jsoup.Helper;
using Org.Jsoup.Nodes;
using iText.IO.Util;

namespace Org.Jsoup.Parser {
    /// <summary>Readers the input stream into tokens.</summary>
    internal sealed class Tokeniser {
        internal const char replacementChar = '\uFFFD';

        private static readonly char[] notCharRefCharsSorted = new char[] { '\t', '\n', '\r', '\f', ' ', '<', '&' };

        static Tokeniser() {
            // replaces null character
            iText.IO.Util.JavaUtil.Sort(notCharRefCharsSorted);
        }

        private CharacterReader reader;

        private ParseErrorList errors;

        private TokeniserState state = TokeniserState.Data;

        private Token emitPending;

        private bool isEmitPending = false;

        private String charsString = null;

        private StringBuilder charsBuilder = new StringBuilder(1024);

        internal StringBuilder dataBuffer = new StringBuilder(1024);

        internal Token.Tag tagPending;

        internal Token.StartTag startPending = new Token.StartTag();

        internal Token.EndTag endPending = new Token.EndTag();

        internal Token.Character charPending = new Token.Character();

        internal Token.Doctype doctypePending = new Token.Doctype();

        internal Token.Comment commentPending = new Token.Comment();

        private String lastStartTag;

        private bool selfClosingFlagAcknowledged = true;

        internal Tokeniser(CharacterReader reader, ParseErrorList errors) {
            // html input
            // errors found while tokenising
            // current tokenisation state
            // the token we are about to emit on next read
            // characters pending an emit. Will fall to charsBuilder if more than one
            // buffers characters to output as one token, if more than one emit per read
            // buffers data looking for </script>
            // tag we are building up
            // doctype building up
            // comment building up
            // the last start tag emitted, to test appropriate end tag
            this.reader = reader;
            this.errors = errors;
        }

        internal Token Read() {
            if (!selfClosingFlagAcknowledged) {
                Error("Self closing flag not acknowledged");
                selfClosingFlagAcknowledged = true;
            }
            while (!isEmitPending) {
                state.Read(this, reader);
            }
            // if emit is pending, a non-character token was found: return any chars in buffer, and leave token for next read:
            if (charsBuilder.Length > 0) {
                String str = charsBuilder.ToString();
                charsBuilder.Delete(0, charsBuilder.Length);
                charsString = null;
                return charPending.Data(str);
            }
            else {
                if (charsString != null) {
                    Token token = charPending.Data(charsString);
                    charsString = null;
                    return token;
                }
                else {
                    isEmitPending = false;
                    return emitPending;
                }
            }
        }

        internal void Emit(Token token) {
            Validate.IsFalse(isEmitPending, "There is an unread token pending!");
            emitPending = token;
            isEmitPending = true;
            if (token.type == Org.Jsoup.Parser.TokenType.StartTag) {
                Token.StartTag startTag = (Token.StartTag)token;
                lastStartTag = startTag.tagName;
                if (startTag.selfClosing) {
                    selfClosingFlagAcknowledged = false;
                }
            }
            else {
                if (token.type == Org.Jsoup.Parser.TokenType.EndTag) {
                    Token.EndTag endTag = (Token.EndTag)token;
                    if (endTag.attributes != null) {
                        Error("Attributes incorrectly present on end tag");
                    }
                }
            }
        }

        internal void Emit(String str) {
            // buffer strings up until last string token found, to emit only one token for a run of character refs etc.
            // does not set isEmitPending; read checks that
            if (charsString == null) {
                charsString = str;
            }
            else {
                if (charsBuilder.Length == 0) {
                    // switching to string builder as more than one emit before read
                    charsBuilder.Append(charsString);
                }
                charsBuilder.Append(str);
            }
        }

        internal void Emit(char[] chars) {
            Emit(iText.IO.Util.JavaUtil.GetStringForChars(chars));
        }

        internal void Emit(char c) {
            Emit(c.ToString());
        }

        internal TokeniserState GetState() {
            return state;
        }

        internal void Transition(TokeniserState state) {
            this.state = state;
        }

        internal void AdvanceTransition(TokeniserState state) {
            reader.Advance();
            this.state = state;
        }

        internal void AcknowledgeSelfClosingFlag() {
            selfClosingFlagAcknowledged = true;
        }

        private readonly char[] charRefHolder = new char[1];

        // holder to not have to keep creating arrays
        internal char[] ConsumeCharacterReference(char? additionalAllowedCharacter, bool inAttribute) {
            if (reader.IsEmpty()) {
                return null;
            }
            if (additionalAllowedCharacter != null && additionalAllowedCharacter == reader.Current()) {
                return null;
            }
            if (reader.MatchesAnySorted(notCharRefCharsSorted)) {
                return null;
            }
            char[] charRef = charRefHolder;
            reader.Mark();
            if (reader.MatchConsume("#")) {
                // numbered
                bool isHexMode = reader.MatchConsumeIgnoreCase("X");
                String numRef = isHexMode ? reader.ConsumeHexSequence() : reader.ConsumeDigitSequence();
                if (numRef.Length == 0) {
                    // didn't match anything
                    CharacterReferenceError("numeric reference with no numerals");
                    reader.RewindToMark();
                    return null;
                }
                if (!reader.MatchConsume(";")) {
                    CharacterReferenceError("missing semicolon");
                }
                // missing semi
                int charval = -1;
                try {
                    int @base = isHexMode ? 16 : 10;
                    charval = System.Convert.ToInt32(numRef, @base);
                }
                catch (FormatException) {
                }
                // skip
                if (charval == -1 || (charval >= 0xD800 && charval <= 0xDFFF) || charval > 0x10FFFF) {
                    CharacterReferenceError("character outside of valid range");
                    charRef[0] = replacementChar;
                    return charRef;
                }
                else {
                    // todo: implement number replacement table
                    // todo: check for extra illegal unicode points as parse errors
                    if (charval < Org.Jsoup.PortUtil.CHARACTER_MIN_SUPPLEMENTARY_CODE_POINT) {
                        charRef[0] = (char)charval;
                        return charRef;
                    }
                    else {
                        return Org.Jsoup.PortUtil.ToChars(charval);
                    }
                }
            }
            else {
                // named
                // get as many letters as possible, and look for matching entities.
                String nameRef = reader.ConsumeLetterThenDigitSequence();
                bool looksLegit = reader.Matches(';');
                // found if a base named entity without a ;, or an extended entity with the ;.
                bool found = (Entities.IsBaseNamedEntity(nameRef) || (Entities.IsNamedEntity(nameRef) && looksLegit));
                if (!found) {
                    reader.RewindToMark();
                    if (looksLegit) {
                        // named with semicolon
                        CharacterReferenceError(MessageFormatUtil.Format("invalid named referenece " + PortUtil.EscapedSingleBracket
                             + "{0}" + PortUtil.EscapedSingleBracket, nameRef));
                    }
                    return null;
                }
                if (inAttribute && (reader.MatchesLetter() || reader.MatchesDigit() || reader.MatchesAny('=', '-', '_'))) {
                    // don't want that to match
                    reader.RewindToMark();
                    return null;
                }
                if (!reader.MatchConsume(";")) {
                    CharacterReferenceError("missing semicolon");
                }
                // missing semi
                charRef[0] = (char)Entities.GetCharacterByName(nameRef);
                return charRef;
            }
        }

        internal Token.Tag CreateTagPending(bool start) {
            tagPending = start ? ((Token.Tag)startPending.Reset()) : ((Token.Tag)endPending.Reset());
            return tagPending;
        }

        internal void EmitTagPending() {
            tagPending.FinaliseTag();
            Emit(tagPending);
        }

        internal void CreateCommentPending() {
            commentPending.Reset();
        }

        internal void EmitCommentPending() {
            Emit(commentPending);
        }

        internal void CreateDoctypePending() {
            doctypePending.Reset();
        }

        internal void EmitDoctypePending() {
            Emit(doctypePending);
        }

        internal void CreateTempBuffer() {
            Token.Reset(dataBuffer);
        }

        internal bool IsAppropriateEndTagToken() {
            return lastStartTag != null && tagPending.tagName.Equals(lastStartTag);
        }

        internal String AppropriateEndTagName() {
            if (lastStartTag == null) {
                return null;
            }
            return lastStartTag;
        }

        internal void Error(TokeniserState state) {
            if (errors.CanAddError()) {
                errors.Add(new ParseError(reader.Pos(), "Unexpected character " + PortUtil.EscapedSingleBracket + "{0}" + 
                    PortUtil.EscapedSingleBracket + " in input state [{}]", reader.Current(), state));
            }
        }

        internal void EofError(TokeniserState state) {
            if (errors.CanAddError()) {
                errors.Add(new ParseError(reader.Pos(), "Unexpectedly reached end of file (EOF) in input state [{0}]", state
                    ));
            }
        }

        private void CharacterReferenceError(String message) {
            if (errors.CanAddError()) {
                errors.Add(new ParseError(reader.Pos(), "Invalid character reference: {0}", message));
            }
        }

        private void Error(String errorMsg) {
            if (errors.CanAddError()) {
                errors.Add(new ParseError(reader.Pos(), errorMsg));
            }
        }

        internal bool CurrentNodeInHtmlNS() {
            // todo: implement namespaces correctly
            return true;
        }

        // Element currentNode = currentNode();
        // return currentNode != null && currentNode.namespace().equals("HTML");
        /// <summary>Utility method to consume reader and unescape entities found within.</summary>
        /// <param name="inAttribute"/>
        /// <returns>unescaped string from reader</returns>
        internal String UnescapeEntities(bool inAttribute) {
            StringBuilder builder = new StringBuilder();
            while (!reader.IsEmpty()) {
                builder.Append(reader.ConsumeTo('&'));
                if (reader.Matches('&')) {
                    reader.Consume();
                    char[] c = ConsumeCharacterReference(null, inAttribute);
                    if (c == null || c.Length == 0) {
                        builder.Append('&');
                    }
                    else {
                        builder.Append(c);
                    }
                }
            }
            return builder.ToString();
        }
    }
}
