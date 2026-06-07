using UnityTextTranslator;
using Xunit;

namespace UnityTextTranslator.Tests
{
    /// <summary>
    /// Эвристики, не дающие «сдвинутому мусору» (одна сторона — число/символы, другая — текст) попасть в
    /// память переводов и разойтись по всем файлам. Корень рассинхрона уже устранён (row.Tag), это страховка —
    /// фиксируем её задокументированное поведение, чтобы будущие правки его не сломали.
    /// </summary>
    public class TranslationMemoryHeuristicsTests
    {
        [Theory]
        [InlineData("250", true)]
        [InlineData("100 / 100", true)]
        [InlineData("$25000", true)]
        [InlineData("100%", true)]
        [InlineData("", false)]      // пусто — переводить нечего, но и «непереводимым токеном» не считаем
        [InlineData("Hunger", false)]
        [InlineData("Голод", false)]
        [InlineData("HV", false)]    // буквы — это текст, а не число/символы
        public void LooksLikeNonTranslatableToken_matches_doc(string s, bool expected)
        {
            Assert.Equal(expected, TranslationMemory.LooksLikeNonTranslatableToken(s));
        }

        [Theory]
        [InlineData("Hunger", "100", true)]    // текст ↔ число = битая пара (сдвиг)
        [InlineData("HV", "250", true)]
        [InlineData("250", "Причёска", true)]
        [InlineData("250", "250", false)]      // обе стороны — число: норм
        [InlineData("Голод", "Hunger", false)] // обе стороны — текст: норм
        public void IsLikelyShiftCorruptedPair_matches_doc(string original, string translated, bool expected)
        {
            Assert.Equal(expected, TranslationMemory.IsLikelyShiftCorruptedPair(original, translated));
        }
    }
}
