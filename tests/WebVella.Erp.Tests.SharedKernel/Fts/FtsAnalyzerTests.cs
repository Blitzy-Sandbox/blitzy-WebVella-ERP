using System;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Fts;

namespace WebVella.Erp.Tests.SharedKernel.Fts
{
    /// <summary>
    /// Comprehensive unit tests for the <see cref="FtsAnalyzer"/> class, the central Bulgarian
    /// Full-Text Search text preprocessing component in the SharedKernel library. The analyzer
    /// exposes a single public method <c>ProcessText(string text)</c> that implements a
    /// three-stage pipeline:
    ///
    ///   1. Tokenization — splits on a fixed set of 12 separator characters with
    ///      <c>StringSplitOptions.RemoveEmptyEntries</c>.
    ///   2. Stop-word filtering — removes tokens that match one of ~260 hardcoded Bulgarian
    ///      stop words. Matching is case-sensitive (the list is all-lowercase).
    ///   3. Stemming — each surviving token is stemmed via <c>BulStem.Stemmer</c> at
    ///      <c>StemmingLevel.Low</c>, which lowercases output internally.
    ///
    /// The final output is a space-delimited string of stemmed tokens with the trailing
    /// space removed (when <c>resultText.Length &gt; 1</c>).
    ///
    /// Tests are grouped into seven categories covering tokenization, stop-word filtering,
    /// stemming integration, output formatting, edge cases, full pipeline validation,
    /// and statelessness — targeting ≥80% code coverage per AAP rule 0.8.2.
    /// </summary>
    public class FtsAnalyzerTests
    {
        /// <summary>
        /// Shared instance used across tests. <see cref="FtsAnalyzer"/> is stateless —
        /// <c>ProcessText</c> creates a new <c>Stemmer</c> and <c>BgStopWords</c> list
        /// on every invocation, so a single instance is safe to reuse.
        /// </summary>
        private readonly FtsAnalyzer _analyzer = new FtsAnalyzer();

        // ──────────────────────────────────────────────────────────────────────
        //  Category 1: Core Tokenization Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that a simple two-word Latin input separated by a single space is
        /// correctly tokenized into two tokens, stemmed (lowercased, no Cyrillic vowels
        /// means the stemmer returns them unchanged), and joined with a space.
        /// </summary>
        [Fact]
        public void ProcessText_BasicTokenization_SplitsOnSpace()
        {
            // Arrange
            string input = "hello world";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — Latin words without Cyrillic vowels pass through the stemmer lowercased
            result.Should().Be("hello world");
        }

        /// <summary>
        /// Verifies that every one of the 12 defined separator characters
        /// (space, hyphen, period, comma, !, ?, ;, :, @, &amp;, /, \) correctly
        /// splits tokens. Each separator is tested individually via <c>[Theory]</c>
        /// with <c>[InlineData]</c> to ensure full coverage of the split character set.
        /// </summary>
        [Theory]
        [InlineData("hello world", ' ', "hello world")]
        [InlineData("hello-world", '-', "hello world")]
        [InlineData("hello.world", '.', "hello world")]
        [InlineData("hello,world", ',', "hello world")]
        [InlineData("hello!world", '!', "hello world")]
        [InlineData("hello?world", '?', "hello world")]
        [InlineData("hello;world", ';', "hello world")]
        [InlineData("hello:world", ':', "hello world")]
        [InlineData("hello@world", '@', "hello world")]
        [InlineData("hello&world", '&', "hello world")]
        [InlineData("hello/world", '/', "hello world")]
        [InlineData("hello\\world", '\\', "hello world")]
        public void ProcessText_AllSeparatorCharacters_SplitsCorrectly(
            string input, char separator, string expected)
        {
            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — each separator should split the input into two tokens
            result.Should().Be(expected,
                because: $"the character '{separator}' (U+{(int)separator:X4}) is a defined separator");
        }

        /// <summary>
        /// Verifies that consecutive separators (e.g. <c>"hello...world"</c>) are
        /// handled by <c>StringSplitOptions.RemoveEmptyEntries</c> — no empty tokens
        /// are produced, and only the two meaningful tokens remain.
        /// </summary>
        [Fact]
        public void ProcessText_ConsecutiveSeparators_RemovesEmptyEntries()
        {
            // Arrange — three consecutive periods between tokens
            string input = "hello...world";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — RemoveEmptyEntries guarantees no phantom empty tokens
            result.Should().Be("hello world");
        }

        /// <summary>
        /// Verifies that a string using many different separator types between tokens
        /// correctly tokenizes every word individually. Uses all 12 separator characters.
        /// </summary>
        [Fact]
        public void ProcessText_MixedSeparators_TokenizesIndividualWords()
        {
            // Arrange — all 12 separator chars used between unique Latin words
            string input = "hello-world.test,foo!bar?baz;qux:quux@corge&grault/garply\\waldo";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — each separator splits; Latin words stemmed = lowercased unchanged
            result.Should().Be("hello world test foo bar baz qux quux corge grault garply waldo");
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Category 2: Stop-Word Filtering Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that well-known Bulgarian stop words from the hardcoded list
        /// (pronouns, prepositions, conjunctions) are removed from the output.
        /// Input is a Bulgarian sentence where stop words "е" and "на" surround
        /// the content words "книгата" and "масата".
        /// </summary>
        [Fact]
        public void ProcessText_BulgarianStopWordsRemoved()
        {
            // Arrange — "книгата е на масата" = "the book is on the table"
            // "е" and "на" are stop words; "книгата" stems to "книга", "масата" stems to "маса"
            string input = "книгата е на масата";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — stop words removed, content words stemmed
            result.Should().Be("книга маса");
            result.Should().NotContain("е");
            result.Should().NotContain("на");
        }

        /// <summary>
        /// Verifies that Bulgarian content words NOT present in the stop word list
        /// pass through the pipeline and appear in the output (stemmed).
        /// "програмиране" (programming) is not a stop word and stems to "програмиран".
        /// </summary>
        [Fact]
        public void ProcessText_NonStopWordsPassThrough()
        {
            // Arrange — "програмиране" is a legitimate Bulgarian content word
            string input = "програмиране";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — the word passes through (stemmed to "програмиран")
            result.Should().NotBeEmpty();
            result.Should().Be("програмиран");
        }

        /// <summary>
        /// Verifies that when the input consists exclusively of Bulgarian stop words,
        /// every token is filtered out and the method returns an empty string.
        /// </summary>
        [Fact]
        public void ProcessText_OnlyStopWords_ReturnsEmpty()
        {
            // Arrange — "и на за от с" are all stop words
            string input = "и на за от с";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — all tokens removed → empty result
            result.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that stop-word filtering is case-sensitive. The stop word list
        /// contains only lowercase entries. Uppercase Bulgarian stop words (e.g. "И", "НА",
        /// "ЗА") do NOT match the list and therefore pass through to the stemmer, which
        /// lowercases them. The output thus contains the lowercased forms even though
        /// their lowercase equivalents are stop words.
        /// </summary>
        [Fact]
        public void ProcessText_StopWordFilterCaseSensitive()
        {
            // Arrange — uppercase variants of stop words "и", "на", "за"
            string input = "И НА ЗА";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — uppercase tokens bypass the case-sensitive stop word filter,
            // then get lowercased by the stemmer
            result.Should().Be("и на за");
        }

        /// <summary>
        /// Verifies that stop-word filtering uses exact whole-word matching via
        /// <c>List&lt;string&gt;.Contains(word)</c>, not substring matching.
        /// The word "безкрайно" contains the stop word "без" as a substring, but since
        /// Contains checks the entire token, "безкрайно" is NOT removed.
        /// </summary>
        [Fact]
        public void ProcessText_StopWordsInLargerWords_NotRemoved()
        {
            // Arrange — "безкрайно" contains stop word "без" as a prefix,
            // but is a distinct token that is NOT in the stop word list
            string input = "безкрайно";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — the word is NOT removed; it passes through and is stemmed
            result.Should().NotBeEmpty();
            // "безкрайно" stems to "безкра" with BulStem Low rules
            result.Should().Be("безкра");
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Category 3: Stemming Integration Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that the BulStem stemmer lowercases all output. The stemmer's
        /// <c>Stem()</c> method calls <c>word.ToLower()</c> internally, so even
        /// uppercase Latin input produces lowercase output.
        /// </summary>
        [Fact]
        public void ProcessText_OutputIsLowercased()
        {
            // Arrange — uppercase Latin text (no Cyrillic vowels → stemmer returns lowercased)
            string input = "HELLO WORLD";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — all characters lowercased by the stemmer
            result.Should().Be("hello world");
        }

        /// <summary>
        /// Verifies correct handling of mixed Bulgarian and Latin text in a single input.
        /// Latin words (no Cyrillic vowels) pass through the stemmer lowercased unchanged.
        /// Bulgarian words with Cyrillic vowels are stemmed according to BulStem Low rules.
        /// </summary>
        [Fact]
        public void ProcessText_MixedBulgarianAndLatin()
        {
            // Arrange — "hello книгата world": Latin words + Bulgarian content word
            string input = "hello книгата world";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — Latin words lowercased, Bulgarian "книгата" stemmed to "книга"
            result.Should().Be("hello книга world");
            result.Should().Contain("hello");
            result.Should().Contain("книга");
            result.Should().Contain("world");
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Category 4: Output Format Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that the output string is space-delimited with no trailing space.
        /// The implementation appends <c>resultWord + " "</c> for each token, then removes
        /// the last character if <c>resultText.Length &gt; 1</c>.
        /// </summary>
        [Fact]
        public void ProcessText_OutputSpaceDelimitedNoTrailingSpace()
        {
            // Arrange — multiple Latin words
            string input = "hello world test";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — no trailing or leading spaces
            result.Should().NotEndWith(" ");
            result.Should().NotStartWith(" ");
            result.Should().Be("hello world test");
        }

        /// <summary>
        /// Verifies that multiple tokens are separated by exactly one space each,
        /// producing the pattern <c>"word1 word2 word3"</c>.
        /// </summary>
        [Fact]
        public void ProcessText_MultipleTokensCorrectSpacing()
        {
            // Arrange
            string input = "one two three";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — exactly single spaces between tokens, no doubles
            result.Should().Be("one two three");
            result.Should().NotContain("  ");
        }

        /// <summary>
        /// Verifies that a single non-stop word input produces the stemmed word
        /// without a trailing space. For a multi-character result, the trailing
        /// space removal condition <c>resultText.Length &gt; 1</c> is satisfied.
        /// </summary>
        [Fact]
        public void ProcessText_SingleNonStopWord()
        {
            // Arrange — single Latin word, not a stop word
            string input = "hello";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — stemmed output, no trailing space
            result.Should().Be("hello");
            result.Should().NotEndWith(" ");
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Category 5: Edge Cases and Empty Input
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that an empty string input returns an empty string. The split
        /// with <c>RemoveEmptyEntries</c> produces an empty array, the foreach
        /// body never executes, and <c>resultText</c> stays as <c>""</c>.
        /// The condition <c>resultText.Length &gt; 1</c> is false for length 0,
        /// so the empty string is returned unmodified.
        /// </summary>
        [Fact]
        public void ProcessText_EmptyString_ReturnsEmpty()
        {
            // Act
            string result = _analyzer.ProcessText("");

            // Assert
            result.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that an input consisting solely of separator characters produces
        /// an empty string. The split with <c>RemoveEmptyEntries</c> produces no tokens.
        /// </summary>
        [Fact]
        public void ProcessText_OnlySeparators_ReturnsEmpty()
        {
            // Arrange — only hyphens, periods, and exclamation marks (all separators)
            string input = "---...!!!";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert
            result.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies behaviour with a single non-Cyrillic character that is not a stop word.
        /// The stemmer receives <c>"x"</c>, lowercases it to <c>"x"</c>, finds no Cyrillic
        /// vowels, and returns <c>"x"</c>. The result string becomes <c>"x "</c> (length 2).
        /// Since <c>Length &gt; 1</c> is true, the trailing space is removed → <c>"x"</c>.
        /// </summary>
        [Fact]
        public void ProcessText_SingleCharacterNonStopWord()
        {
            // Arrange — single Latin character, not in the Bulgarian stop word list
            string input = "x";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — single character survives; trailing space removed because "x " has length 2 > 1
            result.Should().Be("x");
            result.Should().HaveLength(1);
        }

        /// <summary>
        /// Verifies that characters NOT in the separator set remain as part of the token.
        /// The <c>#</c> character is not one of the 12 defined separators, so
        /// <c>"hello#world"</c> is treated as a single token rather than two.
        /// </summary>
        [Fact]
        public void ProcessText_SpecialCharsNotSeparators()
        {
            // Arrange — # is NOT a separator, so this is one token
            string input = "hello#world";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — the entire string is one token, stemmed as a whole
            result.Should().Be("hello#world");
            result.Should().NotContain(" ");
        }

        /// <summary>
        /// Verifies that an input containing only whitespace characters produces
        /// an empty string. Spaces are separators, and <c>RemoveEmptyEntries</c>
        /// eliminates the resulting empty tokens.
        /// </summary>
        [Fact]
        public void ProcessText_OnlyWhitespace_ReturnsEmpty()
        {
            // Arrange — three spaces
            string input = "   ";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert
            result.Should().BeEmpty();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Category 6: Full Pipeline Validation
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates the complete FtsAnalyzer pipeline with a realistic Bulgarian sentence
        /// containing a mix of stop words and content words. Verifies that:
        ///   (a) Stop words are removed ("е", "много", "и", "добро", "за")
        ///   (b) Content words are stemmed ("компютърът"→"компют", "бързо"→"бърз",
        ///       "устройство"→"устройст", "работа"→"работ")
        ///   (c) Output is correctly space-delimited with no trailing space
        /// </summary>
        [Fact]
        public void ProcessText_FullPipelineBulgarianText()
        {
            // Arrange — "компютърът е много бързо и добро устройство за работа"
            // Translation: "the computer is very fast and good device for work"
            // Stop words: "е", "много", "и", "добро", "за"
            // Content words: "компютърът", "бързо", "устройство", "работа"
            string input = "компютърът е много бързо и добро устройство за работа";

            // Act
            string result = _analyzer.ProcessText(input);

            // Assert — stop words removed, content words stemmed via BulStem Low
            result.Should().Be("компют бърз устройст работ");

            // Verify stop words are absent from the output
            result.Should().NotContain("е ");
            result.Should().NotContain("много");
            result.Should().NotContain(" и ");
            result.Should().NotContain("добро");
            result.Should().NotContain("за ");

            // Verify no trailing or leading whitespace
            result.Should().NotStartWith(" ");
            result.Should().NotEndWith(" ");
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Category 7: Statelessness / Consistency
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that creating multiple independent <see cref="FtsAnalyzer"/> instances
        /// and calling <c>ProcessText</c> with the same input produces identical results,
        /// confirming that the analyzer is stateless. Each call creates its own
        /// <c>BgStopWords</c> list and <c>BulStem.Stemmer</c> instance internally.
        /// </summary>
        [Fact]
        public void ProcessText_MultipleInstances_ConsistentResults()
        {
            // Arrange — two separate FtsAnalyzer instances
            var analyzer1 = new FtsAnalyzer();
            var analyzer2 = new FtsAnalyzer();
            string input = "компютърът е на масата";

            // Act — process the same input with different instances
            string result1 = analyzer1.ProcessText(input);
            string result2 = analyzer2.ProcessText(input);

            // Assert — identical output from independent instances
            result1.Should().Be(result2);
            result1.Should().NotBeEmpty();

            // Also verify repeated calls on the same instance are consistent
            string result3 = analyzer1.ProcessText(input);
            result3.Should().Be(result1);
        }
    }
}
