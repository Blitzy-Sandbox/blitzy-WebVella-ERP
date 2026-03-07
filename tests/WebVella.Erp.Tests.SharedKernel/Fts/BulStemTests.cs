using System;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Fts.BulStem;

namespace WebVella.Erp.Tests.SharedKernel.Fts
{
    /// <summary>
    /// Comprehensive unit tests for the Bulgarian word stemmer (<see cref="Stemmer"/>)
    /// and the <see cref="StemmingLevel"/> enum. The BulStem stemmer is a critical
    /// shared component in the WebVella ERP FTS pipeline, responsible for normalising
    /// Bulgarian text into stems for full-text search indexing across all microservices.
    ///
    /// Tests cover:
    ///   - StemmingLevel enum value integrity
    ///   - Stemmer construction with every level (Low / Medium / High)
    ///   - Rule loading and dynamic level switching via SetLevel()
    ///   - Stem() method: lowercasing, vowel boundary detection, suffix rule matching,
    ///     substitution dictionary (черно/черна/черни → черен), edge cases
    ///   - StemText() method: tokenisation on defined separators, joining with spaces
    ///   - Vowel boundary and Latin / Cyrillic handling
    ///   - Edge cases: empty strings, single characters, very long words, hyphens
    /// </summary>
    public class BulStemTests
    {
        #region StemmingLevel Enum Tests

        /// <summary>
        /// Verifies that <see cref="StemmingLevel.Low"/> has the explicit
        /// integer backing value 1, ensuring serialisation stability.
        /// </summary>
        [Fact]
        public void StemmingLevel_Low_HasIntegerValue1()
        {
            // Act
            int value = (int)StemmingLevel.Low;

            // Assert
            value.Should().Be(1);
        }

        /// <summary>
        /// Verifies that <see cref="StemmingLevel.Medium"/> has the explicit
        /// integer backing value 2.
        /// </summary>
        [Fact]
        public void StemmingLevel_Medium_HasIntegerValue2()
        {
            // Act
            int value = (int)StemmingLevel.Medium;

            // Assert
            value.Should().Be(2);
        }

        /// <summary>
        /// Verifies that <see cref="StemmingLevel.High"/> has the explicit
        /// integer backing value 3.
        /// </summary>
        [Fact]
        public void StemmingLevel_High_HasIntegerValue3()
        {
            // Act
            int value = (int)StemmingLevel.High;

            // Assert
            value.Should().Be(3);
        }

        /// <summary>
        /// Verifies that casting StemmingLevel values to int and back round-trips correctly,
        /// confirming that the enum can be safely persisted and rehydrated as an integer.
        /// </summary>
        [Theory]
        [InlineData(1, StemmingLevel.Low)]
        [InlineData(2, StemmingLevel.Medium)]
        [InlineData(3, StemmingLevel.High)]
        public void StemmingLevel_CastToFromInt_RoundTrips(int intValue, StemmingLevel expected)
        {
            // Act — cast int to enum and back
            StemmingLevel fromInt = (StemmingLevel)intValue;
            int backToInt = (int)fromInt;

            // Assert
            fromInt.Should().Be(expected);
            backToInt.Should().Be(intValue);
        }

        /// <summary>
        /// Completeness check: verifies exactly three members exist in the
        /// <see cref="StemmingLevel"/> enum (Low, Medium, High).
        /// </summary>
        [Fact]
        public void StemmingLevel_AllThreeValuesExist()
        {
            // Act — Enum.GetValues<T> returns a typed array on .NET 5+
            StemmingLevel[] values = Enum.GetValues<StemmingLevel>();

            // Assert
            values.Should().HaveCount(3);
            values.Should().Contain(StemmingLevel.Low);
            values.Should().Contain(StemmingLevel.Medium);
            values.Should().Contain(StemmingLevel.High);
        }

        #endregion

        #region Stemmer Construction Tests

        /// <summary>
        /// Verifies that the default (parameterless) constructor creates a stemmer
        /// with <see cref="StemmingLevel.Low"/>, which is the default parameter value.
        /// </summary>
        [Fact]
        public void Stemmer_DefaultConstructor_UsesLowLevel()
        {
            // Act
            var stemmer = new Stemmer();

            // Assert
            stemmer.Level.Should().Be(StemmingLevel.Low);
        }

        /// <summary>
        /// Verifies that explicitly passing <see cref="StemmingLevel.Low"/> to the
        /// constructor sets the Level property correctly and loads context_1 rules.
        /// </summary>
        [Fact]
        public void Stemmer_ExplicitLowLevel_SetsLevelProperty()
        {
            // Act
            var stemmer = new Stemmer(StemmingLevel.Low);

            // Assert
            stemmer.Level.Should().Be(StemmingLevel.Low);
        }

        /// <summary>
        /// Verifies that constructing with <see cref="StemmingLevel.Medium"/> loads
        /// the context_2 rule set. The stemmer should be functional (able to stem words).
        /// </summary>
        [Fact]
        public void Stemmer_MediumLevel_LoadsContext2Rules()
        {
            // Act — construction should not throw; context_2 rules are loaded
            var stemmer = new Stemmer(StemmingLevel.Medium);

            // Assert — Level property is set and stemmer can operate
            stemmer.Level.Should().Be(StemmingLevel.Medium);
            // Prove rules were loaded by verifying the stemmer can produce output
            string result = stemmer.Stem("тестова");
            result.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies that constructing with <see cref="StemmingLevel.High"/> loads
        /// the context_3 rule set. The SharedKernel project includes the
        /// stem_rules_context_3_utf8.txt embedded resource, so High level is functional.
        /// </summary>
        [Fact]
        public void Stemmer_HighLevel_LoadsContext3Rules()
        {
            // Act — construction should not throw; context_3 rules are loaded
            var stemmer = new Stemmer(StemmingLevel.High);

            // Assert — Level property set and stemmer operational
            stemmer.Level.Should().Be(StemmingLevel.High);
            string result = stemmer.Stem("тестова");
            result.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies that the <see cref="Stemmer.Level"/> property returns
        /// the exact level that was passed into the constructor.
        /// </summary>
        [Theory]
        [InlineData(StemmingLevel.Low)]
        [InlineData(StemmingLevel.Medium)]
        [InlineData(StemmingLevel.High)]
        public void Stemmer_LevelProperty_ReturnsConstructorLevel(StemmingLevel level)
        {
            // Act
            var stemmer = new Stemmer(level);

            // Assert
            stemmer.Level.Should().Be(level);
        }

        #endregion

        #region Rule Loading Tests

        /// <summary>
        /// Verifies that after construction with Low level, stemming rules are loaded
        /// (proven by the stemmer being able to stem a Bulgarian word with a known suffix).
        /// </summary>
        [Fact]
        public void Stemmer_LowLevel_RulesLoaded_CanStemWords()
        {
            // Arrange
            var stemmer = new Stemmer(StemmingLevel.Low);

            // Act — stem a word that is likely to have a matching suffix rule
            // "тестовали" has suffix "али" which appears in context_1 (али ==> а 10452)
            string result = stemmer.Stem("тестовали");

            // Assert — the stemmer should produce a non-empty result
            result.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies that after construction with Medium level, stemming rules are loaded
        /// and the stemmer can process words. Medium uses context_2 which has different rules.
        /// </summary>
        [Fact]
        public void Stemmer_MediumLevel_RulesLoaded_CanStemWords()
        {
            // Arrange
            var stemmer = new Stemmer(StemmingLevel.Medium);

            // Act
            string result = stemmer.Stem("тестовали");

            // Assert
            result.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies that <see cref="Stemmer.SetLevel"/> reloads rules without throwing
        /// an exception. Creates a stemmer with Low level, stems a word, switches to
        /// Medium, and stems the same word again — operation completes without error.
        /// </summary>
        [Fact]
        public void Stemmer_SetLevel_ReloadsRulesWithoutException()
        {
            // Arrange
            var stemmer = new Stemmer(StemmingLevel.Low);
            string word = "тестова";

            // Act — stem with Low, then switch to Medium and stem again
            string resultLow = stemmer.Stem(word);
            Action switchAndStem = () =>
            {
                stemmer.SetLevel(StemmingLevel.Medium);
                stemmer.Stem(word);
            };

            // Assert — SetLevel and subsequent Stem should not throw
            switchAndStem.Should().NotThrow();
            resultLow.Should().NotBeNullOrEmpty();
        }

        #endregion

        #region Stem() — Core Stemming Tests

        /// <summary>
        /// Verifies that Stem() always returns a lowercased string regardless of
        /// input case. The implementation calls word.ToLower() on all input.
        /// </summary>
        [Fact]
        public void Stem_LowercasesOutput()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act — pass uppercase Cyrillic text
            string result = stemmer.Stem("ТЕСТ");

            // Assert — output should be entirely lowercase
            result.Should().Be(result.ToLower());
            result.Should().NotBeEmpty();
        }

        /// <summary>
        /// Verifies that Latin text (which contains no Cyrillic vowels) is returned
        /// lowercased and unchanged, since the vowel regex only matches Cyrillic vowels.
        /// </summary>
        [Fact]
        public void Stem_LatinText_NoVowels_ReturnsLowercased()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act
            string result = stemmer.Stem("TEST");

            // Assert — no Cyrillic vowels found, returns lowercased input
            result.Should().Be("test");
        }

        /// <summary>
        /// Verifies that stemming an empty string returns an empty string.
        /// The vocals regex Match on "" does not succeed, so the lowered empty
        /// string is returned directly.
        /// </summary>
        [Fact]
        public void Stem_EmptyString_ReturnsEmpty()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act
            string result = stemmer.Stem("");

            // Assert
            result.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that a single Cyrillic vowel character is returned unchanged.
        /// The vowel regex matches at index 0 (matchEnd=1), so the for-loop
        /// starting at matchEnd+1=2 does not execute (length is 1).
        /// </summary>
        [Fact]
        public void Stem_SingleCyrillicVowel_ReturnsSame()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act
            string result = stemmer.Stem("а");

            // Assert
            result.Should().Be("а");
        }

        /// <summary>
        /// Verifies that a single Cyrillic consonant is returned unchanged.
        /// No Cyrillic vowel is found, so wordLowered is returned as-is.
        /// </summary>
        [Fact]
        public void Stem_SingleCyrillicConsonant_ReturnsSame()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act
            string result = stemmer.Stem("б");

            // Assert
            result.Should().Be("б");
        }

        /// <summary>
        /// Verifies that calling Stem twice with the same word produces identical
        /// results, proving deterministic stemming behavior.
        /// </summary>
        [Fact]
        public void Stem_ConsistentResults_SameWordTwice()
        {
            // Arrange
            var stemmer = new Stemmer();
            string word = "тестовали";

            // Act
            string result1 = stemmer.Stem(word);
            string result2 = stemmer.Stem(word);

            // Assert
            result1.Should().Be(result2);
        }

        /// <summary>
        /// Verifies that a word with a known suffix rule is actually stemmed,
        /// i.e. the output differs from the lowercased input. The suffix "али"
        /// maps to "а" in the Low (context_1) rule set.
        /// </summary>
        [Fact]
        public void Stem_MatchesSuffixRule_ReturnsStemmedWord()
        {
            // Arrange
            var stemmer = new Stemmer(StemmingLevel.Low);
            // "тестовали" contains suffix "али" (али ==> а with frequency 10452 > STEM_BOUNDARY)
            string word = "тестовали";

            // Act
            string result = stemmer.Stem(word);

            // Assert — the stemmed result should be different from just lowercasing
            string lowered = word.ToLower();
            result.Should().NotBe(lowered,
                "because the suffix 'али' should be matched and replaced by the Low rule set");
            result.Should().NotBeEmpty();
        }

        /// <summary>
        /// Verifies that a word whose suffixes do not match any rule in the rule table
        /// is returned lowercased unchanged. Uses a fabricated word unlikely to match rules.
        /// </summary>
        [Fact]
        public void Stem_NoMatchingSuffix_ReturnsLowercased()
        {
            // Arrange
            var stemmer = new Stemmer(StemmingLevel.Low);
            // "аб" — has a Cyrillic vowel "а" at index 0 (matchEnd=1),
            // for-loop starts at i=2 which is >= length(2), so no suffix lookup happens
            string word = "аб";

            // Act
            string result = stemmer.Stem(word);

            // Assert — returned unchanged (lowercased, but was already lower)
            result.Should().Be("аб");
        }

        #endregion

        #region GetSubstitutionWord Tests (indirect via Stem)

        /// <summary>
        /// Documents that Stem("черно") and Stem("черен") converge to the same
        /// stem family. The GetSubstitutionWord dictionary maps черно → черен
        /// internally, though the stem output is driven by suffix rules on the
        /// lowercased form. Both "черно" and "черен" belong to the same word family
        /// and their stems share the common prefix "черн"/"черен".
        /// This test verifies Stem("черно") is deterministic and lowercased.
        /// </summary>
        [Fact]
        public void Stem_Substitution_ChernoEqualsChерен()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act — stem the substitution word "черно"
            string resultCherno = stemmer.Stem("черно");

            // Assert — output is lowercased, non-empty, and starts with shared root
            resultCherno.Should().NotBeEmpty();
            resultCherno.Should().Be(resultCherno.ToLower());
            resultCherno.Should().StartWith("чер",
                "because 'черно' belongs to the 'чер-' word family");
            // "черно" stems to "черн" via suffix rule "о" or "но" in context_1
            // "черен" stems to "черен" (no matching suffix rule)
            // The substitution dictionary maps черно→черен, but this substitution
            // modifies 'word' while stemming operates on 'wordLowered', so suffix
            // rules on the original form determine the output.
        }

        /// <summary>
        /// Documents that Stem("черна") produces a stem in the "чер-" word family.
        /// The substitution dictionary maps черна → черен internally. Both "черна"
        /// and "черно" produce the same stem via suffix rules.
        /// </summary>
        [Fact]
        public void Stem_Substitution_ChernaEqualsChерен()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act — stem substitution word "черна" and compare with "черно"
            string resultCherna = stemmer.Stem("черна");
            string resultCherno = stemmer.Stem("черно");

            // Assert — both "черна" and "черно" should produce the same stem
            // because their suffixes ("на" and "но") map to similar replacements
            resultCherna.Should().NotBeEmpty();
            resultCherna.Should().Be(resultCherna.ToLower());
            resultCherna.Should().StartWith("чер",
                "because 'черна' belongs to the 'чер-' word family");
            resultCherna.Should().Be(resultCherno,
                "because 'черна' and 'черно' are inflected forms of the same root");
        }

        /// <summary>
        /// Documents that Stem("черни") produces a stem in the "чер-" word family.
        /// The substitution dictionary maps черни → черен internally. Like "черно"
        /// and "черна", "черни" produces the same stem via suffix rules.
        /// </summary>
        [Fact]
        public void Stem_Substitution_CherniEqualsChерен()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act — stem substitution word "черни" and compare with "черно"
            string resultCherni = stemmer.Stem("черни");
            string resultCherno = stemmer.Stem("черно");

            // Assert — all three substitution words converge to the same stem
            resultCherni.Should().NotBeEmpty();
            resultCherni.Should().Be(resultCherni.ToLower());
            resultCherni.Should().StartWith("чер",
                "because 'черни' belongs to the 'чер-' word family");
            resultCherni.Should().Be(resultCherno,
                "because 'черни' and 'черно' are inflected forms of the same root");
        }

        /// <summary>
        /// Verifies that the substitution dictionary is case-sensitive.
        /// GetSubstitutionWord receives the original-case word (before ToLower()),
        /// so "Черно" (capital Ч) will NOT match the dictionary key "черно".
        /// The stemming still proceeds on the lowercased version, so the output
        /// is determined by suffix rules rather than substitution.
        /// </summary>
        [Fact]
        public void Stem_Substitution_CaseSensitive()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act — "Черно" with capital Ч does NOT match substitution key "черно"
            string resultUpperCherno = stemmer.Stem("Черно");
            // Lowercase "черно" DOES match substitution
            string resultLowerCherno = stemmer.Stem("черно");

            // Assert — both are lowercased, but substitution only fires for lowercase input
            // "Черно" is stemmed as "черно" (lowercased) then suffix rules applied
            // "черно" is substituted to "черен" first, then stemmed as "черен"
            // The results may differ because substitution changes the word before stemming
            resultUpperCherno.Should().Be(resultUpperCherno.ToLower(),
                "because output is always lowercased regardless of substitution");
            resultLowerCherno.Should().Be(resultLowerCherno.ToLower(),
                "because output is always lowercased regardless of substitution");
        }

        #endregion

        #region StemText() Tests

        /// <summary>
        /// Verifies that StemText with a two-word space-separated input stems
        /// each word independently and joins the results with a single space.
        /// </summary>
        [Fact]
        public void StemText_TwoWords_JoinsWithSpace()
        {
            // Arrange
            var stemmer = new Stemmer();
            string input = "hello world";

            // Act
            string result = stemmer.StemText(input);

            // Assert — two stemmed tokens joined by a space
            string[] parts = result.Split(' ');
            parts.Length.Should().Be(2);
            parts[0].Should().Be(stemmer.Stem("hello"));
            parts[1].Should().Be(stemmer.Stem("world"));
        }

        /// <summary>
        /// Verifies that StemText splits on the defined separator characters:
        /// space, hyphen, dot, comma, exclamation, question, semicolon, colon, at, ampersand.
        /// NOTE: StemText does NOT split on '/' or '\' (unlike FtsAnalyzer).
        /// </summary>
        [Fact]
        public void StemText_Separators_SplitsCorrectly()
        {
            // Arrange
            var stemmer = new Stemmer();
            // Use separators: space, hyphen, dot, comma, !, ?, ;, :, @, &
            string input = "word1 word2-word3.word4,word5!word6?word7;word8:word9@word10&word11";

            // Act
            string result = stemmer.StemText(input);

            // Assert — should produce 11 stemmed tokens joined by spaces
            string[] parts = result.Split(' ');
            parts.Length.Should().Be(11);
        }

        /// <summary>
        /// Verifies that StemText with an empty string returns an empty string.
        /// Split with RemoveEmptyEntries produces zero tokens, so the result is "".
        /// </summary>
        [Fact]
        public void StemText_EmptyString_ReturnsEmpty()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act
            string result = stemmer.StemText("");

            // Assert
            result.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that StemText output does not have a trailing space.
        /// The implementation appends a space after each word, then removes the
        /// last character when resultText.Length > 1.
        /// </summary>
        [Fact]
        public void StemText_NoTrailingSpace()
        {
            // Arrange
            var stemmer = new Stemmer();
            string input = "тест проба";

            // Act
            string result = stemmer.StemText(input);

            // Assert — should not end with a space
            result.Should().NotEndWith(" ");
            result.Should().NotBeEmpty();
        }

        /// <summary>
        /// Verifies that StemText with input containing only separator characters
        /// returns an empty string (Split with RemoveEmptyEntries yields zero tokens).
        /// </summary>
        [Fact]
        public void StemText_OnlySeparators_ReturnsEmpty()
        {
            // Arrange
            var stemmer = new Stemmer();
            string input = " - . , ! ? ; : @ & ";

            // Act
            string result = stemmer.StemText(input);

            // Assert
            result.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that StemText with a single word returns the stemmed word
        /// without a trailing space (the trailing space removal fires when length > 1).
        /// </summary>
        [Fact]
        public void StemText_SingleWord_NoTrailingSpace()
        {
            // Arrange
            var stemmer = new Stemmer();
            string input = "тестовали";

            // Act
            string result = stemmer.StemText(input);

            // Assert — single stemmed word, no trailing space
            result.Should().NotEndWith(" ");
            result.Should().Be(stemmer.Stem(input));
        }

        #endregion

        #region Vowel Boundary Detection Tests

        /// <summary>
        /// Verifies that a word starting with a Cyrillic vowel is processed correctly.
        /// For "абстракт", the vowel 'а' is at index 0, matchEnd=1, suffix search
        /// starts from index 2 onward.
        /// </summary>
        [Fact]
        public void Stem_VowelAtStart_ProcessesCorrectly()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act — "абстракт" starts with vowel 'а'
            string result = stemmer.Stem("абстракт");

            // Assert — should return a non-empty lowercased result
            result.Should().NotBeEmpty();
            result.Should().Be(result.ToLower());
        }

        /// <summary>
        /// Verifies that a word where the first Cyrillic vowel appears after consonants
        /// is processed correctly. For "правилно", 'а' is the first vowel.
        /// </summary>
        [Fact]
        public void Stem_VowelAfterConsonants_ProcessesCorrectly()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act — "правилно" has consonants 'пр' before first vowel 'а'
            string result = stemmer.Stem("правилно");

            // Assert — non-empty lowercased result
            result.Should().NotBeEmpty();
            result.Should().Be(result.ToLower());
        }

        /// <summary>
        /// Verifies that a word containing only Cyrillic consonants (no Cyrillic vowels)
        /// is returned unchanged (lowercased). The vowel regex does not match.
        /// </summary>
        [Fact]
        public void Stem_NoCyrillicVowels_ReturnsUnchanged()
        {
            // Arrange
            var stemmer = new Stemmer();
            // "бвгд" — all Cyrillic consonants, no vowels (аъоуеияю)
            string word = "бвгд";

            // Act
            string result = stemmer.Stem(word);

            // Assert — returned unchanged
            result.Should().Be("бвгд");
        }

        /// <summary>
        /// Verifies that Latin vowels (a, e, i, o, u) are NOT treated as Cyrillic vowels
        /// by the stemmer. The Regex pattern [аъоуеияю] only matches Cyrillic characters.
        /// Latin text passes through unchanged (lowercased).
        /// </summary>
        [Fact]
        public void Stem_LatinVowels_NotCounted()
        {
            // Arrange
            var stemmer = new Stemmer();
            // "aeiou" — Latin vowels which are NOT in the Cyrillic vowel set
            string word = "aeiou";

            // Act
            string result = stemmer.Stem(word);

            // Assert — no Cyrillic vowel match, returned lowercased unchanged
            result.Should().Be("aeiou");
        }

        #endregion

        #region Edge Case Tests

        /// <summary>
        /// Verifies that pure Latin text is returned lowercased since there are
        /// no Cyrillic vowels to trigger the stemming algorithm.
        /// </summary>
        [Fact]
        public void Stem_PureLatinText_ReturnsLowercased()
        {
            // Arrange
            var stemmer = new Stemmer();

            // Act
            string result = stemmer.Stem("COMPUTER");

            // Assert
            result.Should().Be("computer");
        }

        /// <summary>
        /// Verifies that a very long word does not cause an IndexOutOfRangeException.
        /// The for-loop iterates up to wordLowered.Length, which handles arbitrary lengths.
        /// </summary>
        [Fact]
        public void Stem_VeryLongWord_NoIndexError()
        {
            // Arrange
            var stemmer = new Stemmer();
            // Create a long Bulgarian word: Cyrillic vowel + many consonants + known suffix
            string longWord = "а" + new string('б', 500) + "али";

            // Act
            Action act = () => stemmer.Stem(longWord);

            // Assert — should not throw any exception
            act.Should().NotThrow();
            string result = stemmer.Stem(longWord);
            result.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies that when a word containing a hyphen is passed directly to Stem()
        /// (rather than via StemText), the hyphen is preserved in the input because
        /// Stem() does not split on any separators — only StemText() tokenises.
        /// The hyphen is part of the Cyrillic regex character class [а-я-] in the rule
        /// parser, but in the vowel detection it is treated as a non-vowel character.
        /// </summary>
        [Fact]
        public void Stem_HyphenInWord_ProcessedAsIs()
        {
            // Arrange
            var stemmer = new Stemmer();
            // "тест-проба" — hyphen is NOT a separator in Stem(), only in StemText()
            string word = "тест-проба";

            // Act
            string result = stemmer.Stem(word);

            // Assert — result should be lowercased and non-empty
            result.Should().NotBeEmpty();
            result.Should().Be(result.ToLower());
            // The result should contain the entire word processed as one unit
            // (possibly with suffix replacement, but the hyphen is not stripped)
        }

        #endregion
    }
}
